#include <gtk/gtk.h>
#include <glib/gstdio.h>
#include <json-glib/json-glib.h>
#include "../../../src/linux/native/fcitx5/typelay-fcitx-v1.h"

/* Only the isolated test executable writes this evidence. Production has no
 * command to dump source text, inject input or change a test model. */
static GtkWidget *report_window, *report_first, *report_second, *other_window, *other_entry;
static GtkWidget *report_multiline, *excluded_views[3];
static const char *report_path;
static char *last_report;
static guint report_source_id;
static gboolean report_closed;
static void fixture_destroyed(GtkWidget *window, gpointer unused) {
    (void)window; (void)unused;
    /* destroy is RUN_CLEANUP: stop before the class handler destroys children.
     * gtk_main_quit alone does not cancel already-ready timeout dispatch. */
    report_closed = TRUE;
    guint source = report_source_id;
    report_source_id = 0;
    if (source) g_source_remove(source);
    gtk_main_quit();
}
typedef struct {
    guint insertions, deletions, focus_in, focus_out;
    gint64 focus_in_at_us;
} EntryEvidence;
static EntryEvidence first_evidence, second_evidence;
static JsonObject *first_insert_context, *first_delete_context;
#include "gtk_reentry.inc"
#include "gtk_input_notifications.inc"
#include "gtk_direct_epoch.inc"

static guint function_down[24], function_up[24];
static gboolean observe_key(GtkWidget *widget, GdkEventKey *event, gpointer data) {
    (void)widget; (void)data;
    if (event->keyval >= GDK_KEY_F1 && event->keyval <= GDK_KEY_F24) {
        guint index = event->keyval - GDK_KEY_F1;
        if (event->type == GDK_KEY_PRESS) function_down[index]++;
        else function_up[index]++;
    }
    return FALSE;
}
static GtkWidget *entry_client(GtkWidget *widget) {
    return widget;
}
static void focus_changed(GObject *object, GParamSpec *spec, gpointer data) {
    (void)spec;
    EntryEvidence *evidence = data;
    if (gtk_widget_has_focus(GTK_WIDGET(object))) {
        evidence->focus_in++;
        evidence->focus_in_at_us = g_get_monotonic_time();
    }
    else evidence->focus_out++;
}
static void remember_entry_change(EntryEvidence *evidence, JsonObject **observation) {
    if (evidence != &first_evidence) return;
    if (*observation) json_object_unref(*observation);
    JsonObject *context = *observation = json_object_new();
    json_object_set_int_member(context, "atUs", g_get_monotonic_time());
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(report_second), &start, &end);
    json_object_set_boolean_member(context, "secondFocused",
        gtk_widget_has_focus(entry_client(report_second)));
    json_object_set_string_member(context, "secondText", gtk_entry_get_text(GTK_ENTRY(report_second)));
    json_object_set_int_member(context, "secondStart", start);
    json_object_set_int_member(context, "secondEnd", end);
    json_object_set_int_member(context, "firstFocusIn", first_evidence.focus_in);
    json_object_set_int_member(context, "firstFocusOut", first_evidence.focus_out);
}
static void entry_inserted(GtkEntryBuffer *buffer, guint position, const char *text, guint count, gpointer data) {
    (void)buffer; (void)position; (void)text; (void)count;
    EntryEvidence *evidence = data;
    evidence->insertions++;
    remember_entry_change(evidence, &first_insert_context);
    if (evidence == &first_evidence) mutation_barrier("insert");
}
static void entry_deleted(GtkEntryBuffer *buffer, guint position, guint count, gpointer data) {
    (void)buffer; (void)position; (void)count;
    EntryEvidence *evidence = data;
    evidence->deletions++;
    remember_entry_change(evidence, &first_delete_context);
    if (evidence == &first_evidence) mutation_barrier("delete");
}
static void watch_entry(GtkWidget *widget, EntryEvidence *evidence) {
    GtkEntryBuffer *buffer = gtk_entry_get_buffer(GTK_ENTRY(widget));
    g_signal_connect_after(buffer, "inserted-text", G_CALLBACK(entry_inserted), evidence);
    g_signal_connect_after(buffer, "deleted-text", G_CALLBACK(entry_deleted), evidence);
    g_signal_connect(entry_client(widget), "notify::has-focus", G_CALLBACK(focus_changed), evidence);
    g_signal_connect_after(widget, "notify::cursor-position", G_CALLBACK(selection_changed), evidence);
    g_signal_connect_after(widget, "notify::selection-bound", G_CALLBACK(selection_changed), evidence);
}
static guint user_actions_started, user_actions_finished;
static void action_started(GtkTextBuffer *buffer, gpointer data) {
    (void)buffer; (void)data; user_actions_started++;
}
static void action_finished(GtkTextBuffer *buffer, gpointer data) {
    (void)buffer; (void)data; user_actions_finished++;
}

static gboolean inspect_ime(GtkWidget *widget, TlFcitxStateV1 *state) {
    GtkIMContext *context = widget ? g_object_get_data(G_OBJECT(widget), TL_FCITX_CONTEXT_V1) : NULL;
    if (!context || !GTK_IS_IM_CONTEXT(context)) return FALSE;
    g_object_ref(context);
    const TlFcitxCapabilityV1 *cap = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    gboolean valid = cap && cap->size == sizeof(*cap) && cap->version == TL_FCITX_ABI_V1
        && cap->inspect && cap->inspect(context, widget, state);
    g_object_unref(context);
    return valid;
}
static gboolean ime_clear(const TlFcitxStateV1 *state) {
    return state->flags == (TL_FCITX_KNOWN | TL_FCITX_FOCUSED)
        && state->server_serial == state->applied_serial;
}
/* These are observations of the real widgets, never inputs to the provider. */
static void report_entry(JsonBuilder *builder, const char *name, GtkWidget *widget, const EntryEvidence *evidence) {
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(widget), &start, &end);
    json_builder_set_member_name(builder, name);
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "focused");
    json_builder_add_boolean_value(builder, gtk_widget_has_focus(entry_client(widget)));
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, start);
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, end);
    json_builder_set_member_name(builder, "insertions"); json_builder_add_int_value(builder, evidence->insertions);
    json_builder_set_member_name(builder, "deletions"); json_builder_add_int_value(builder, evidence->deletions);
    json_builder_set_member_name(builder, "focusInAtUs"); json_builder_add_int_value(builder, evidence->focus_in_at_us);
    json_builder_set_member_name(builder, "focusIn"); json_builder_add_int_value(builder, evidence->focus_in);
    json_builder_set_member_name(builder, "focusOut"); json_builder_add_int_value(builder, evidence->focus_out);
    gint x, y;
    if (gtk_widget_translate_coordinates(widget, report_window, 0, 0, &x, &y)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
        json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_width(widget));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_height(widget));
    }
    TlFcitxStateV1 state = { .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
    gboolean valid = inspect_ime(widget, &state);
    json_builder_set_member_name(builder, "ime");
    if (valid) {
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "flags"); json_builder_add_int_value(builder, state.flags);
        json_builder_set_member_name(builder, "context"); json_builder_add_int_value(builder, state.context_generation);
        json_builder_set_member_name(builder, "input"); json_builder_add_int_value(builder, state.input_epoch);
        json_builder_set_member_name(builder, "server"); json_builder_add_int_value(builder, state.server_serial);
        json_builder_set_member_name(builder, "applied"); json_builder_add_int_value(builder, state.applied_serial);
        json_builder_end_object(builder);
    } else json_builder_add_null_value(builder);
    json_builder_end_object(builder);
}
static void find_candidate(GtkWidget *widget, gpointer data) {
    watch_session_button(widget);
    GtkWidget **candidate = data;
    if (GTK_IS_TEXT_VIEW(widget)
        && g_strcmp0(gtk_buildable_get_name(GTK_BUILDABLE(widget)), "typelay-candidate") == 0)
        *candidate = widget;
    else if (GTK_IS_CONTAINER(widget)) gtk_container_forall(GTK_CONTAINER(widget), find_candidate, data);
}

/* Test-local identities survive observations without exposing native addresses. */
static guint language_instance(GObject *object) {
    static guint next;
    if (!object) return 0;
    guint instance = GPOINTER_TO_UINT(g_object_get_data(object, "fixture-language-instance"));
    if (!instance) {
        instance = ++next;
        g_object_set_data(object, "fixture-language-instance", GUINT_TO_POINTER(instance));
    }
    return instance;
}
/* Bounded test-only evidence for real child-popup lifecycle and focus edges.
 * No focus operations, main-loop iteration, native addresses or source text. */
static JsonObject *language_transitions[32];
static guint language_transition_count;
static gboolean language_watched;
static JsonObject *language_transition(const char *event, GtkWidget *focus) {
    if (!language_watched || language_transition_count == G_N_ELEMENTS(language_transitions)) return NULL;
    JsonObject *value = language_transitions[language_transition_count++] = json_object_new();
    json_object_set_int_member(value, "atMonoMs", g_get_monotonic_time() / 1000);
    json_object_set_string_member(value, "event", event);
    json_object_set_int_member(value, "focusInstance", language_instance(focus ? G_OBJECT(focus) : NULL));
    json_object_set_string_member(value, "focusType", focus ? G_OBJECT_TYPE_NAME(focus) : "none");
    json_object_set_boolean_member(value, "focusMapped", focus && gtk_widget_get_mapped(focus));
    json_object_set_boolean_member(value, "windowActive", gtk_window_is_active(GTK_WINDOW(report_window)));
    return value;
}
static void observe_language_popup(GtkWidget *button, const char *event) {
    GtkPopover *popup = gtk_menu_button_get_popover(GTK_MENU_BUTTON(button));
    if (!popup) return;
    JsonObject *value = language_transition(event, gtk_window_get_focus(GTK_WINDOW(report_window)));
    if (!value) return;
    json_object_set_string_member(value, "control", gtk_widget_get_name(button));
    json_object_set_boolean_member(value, "active", gtk_toggle_button_get_active(GTK_TOGGLE_BUTTON(button)));
    json_object_set_boolean_member(value, "visible", gtk_widget_get_visible(GTK_WIDGET(popup)));
    json_object_set_boolean_member(value, "mapped", gtk_widget_get_mapped(GTK_WIDGET(popup)));
}
static void language_focus_changed(GtkWindow *window, GtkWidget *destination, gpointer unused) {
    (void)window; (void)unused;
    /* set-focus's argument is the real destination; the getter may be in an
     * intermediate state while GTK is dispatching this same transition. */
    language_transition("set-focus", destination);
}
static void language_active_changed(GObject *button, GParamSpec *spec, gpointer data) {
    (void)spec; (void)data;
    observe_language_popup(GTK_WIDGET(button), "active");
}
static void language_visible_changed(GObject *popup, GParamSpec *spec, gpointer data) {
    (void)popup; (void)spec;
    observe_language_popup(data, "visible");
}
static void language_mapped(GtkWidget *popup, gpointer data) {
    (void)popup;
    observe_language_popup(data, "map");
}
static void language_unmapped(GtkWidget *popup, gpointer data) {
    (void)popup;
    observe_language_popup(data, "unmap");
}
static void watch_language_popup(GtkWidget *button, GtkPopover *popup) {
    if (g_object_get_data(G_OBJECT(button), "fixture-language-popup-watched")) return;
    language_watched = TRUE;
    g_object_set_data(G_OBJECT(button), "fixture-language-popup-watched", GINT_TO_POINTER(1));
    g_signal_connect_after(button, "notify::active", G_CALLBACK(language_active_changed), NULL);
    g_signal_connect_object(popup, "notify::visible", G_CALLBACK(language_visible_changed), button, G_CONNECT_AFTER);
    g_signal_connect_object(popup, "map", G_CALLBACK(language_mapped), button, G_CONNECT_AFTER);
    g_signal_connect_object(popup, "unmap", G_CALLBACK(language_unmapped), button, G_CONNECT_AFTER);
}
static gboolean language_control(const char *name) {
    return g_strcmp0(name, "typelay-menu-trigger") == 0
        || g_strcmp0(name, "typelay-source-language") == 0
        || g_strcmp0(name, "typelay-target-language") == 0
        || g_strcmp0(name, "typelay-apply-languages") == 0
        || g_strcmp0(name, "typelay-language-menu") == 0;
}
/* Observe natural GTK frame completion for a changing trigger label. No frame
 * is requested here, and the frame callbacks never retain the button. */
typedef struct {
    guint revision, frame_revision, painted_revision;
    gint64 frame_counter;
    gint width, height;
    GdkFrameClock *clock;
} TriggerLayout;
static void free_trigger_layout(gpointer data) {
    TriggerLayout *layout = data;
    g_clear_object(&layout->clock);
    g_free(layout);
}
static void trigger_label_changed(GObject *button, GParamSpec *property, gpointer data) {
    (void)property; (void)data;
    TriggerLayout *layout = g_object_get_data(button, "fixture-trigger-layout");
    if (layout->revision) layout->revision = layout->revision == G_MAXUINT ? 0 : layout->revision + 1;
}
static void trigger_frame_started(GdkFrameClock *clock, GtkWidget *button) {
    TriggerLayout *layout = g_object_get_data(G_OBJECT(button), "fixture-trigger-layout");
    gint64 frame = gdk_frame_clock_get_frame_counter(clock);
    if (layout->clock == clock && layout->frame_counter != frame) {
        layout->frame_counter = frame;
        layout->frame_revision = layout->revision;
    }
}
static void trigger_frame_finished(GdkFrameClock *clock, GtkWidget *button) {
    TriggerLayout *layout = g_object_get_data(G_OBJECT(button), "fixture-trigger-layout");
    if (layout->clock != clock || gtk_widget_get_frame_clock(button) != clock
        || layout->frame_counter != gdk_frame_clock_get_frame_counter(clock)
        || !gtk_widget_get_mapped(button) || !layout->revision
        || layout->frame_revision != layout->revision) return;
    layout->painted_revision = layout->revision;
    layout->width = gtk_widget_get_allocated_width(button);
    layout->height = gtk_widget_get_allocated_height(button);
}
static void trigger_unmapped(GtkWidget *button, gpointer data) {
    (void)data;
    TriggerLayout *layout = g_object_get_data(G_OBJECT(button), "fixture-trigger-layout");
    layout->frame_revision = layout->painted_revision = 0;
}
static TriggerLayout *watch_trigger_layout(GtkWidget *button) {
    TriggerLayout *layout = g_object_get_data(G_OBJECT(button), "fixture-trigger-layout");
    if (!layout) {
        layout = g_new0(TriggerLayout, 1);
        layout->revision = 1;
        layout->frame_counter = -1;
        g_object_set_data_full(G_OBJECT(button), "fixture-trigger-layout", layout, free_trigger_layout);
        g_signal_connect(button, "notify::label", G_CALLBACK(trigger_label_changed), NULL);
        g_signal_connect(button, "unmap", G_CALLBACK(trigger_unmapped), NULL);
    }
    GdkFrameClock *clock = gtk_widget_get_frame_clock(button);
    if (clock && layout->clock != clock) {
        if (layout->clock) g_signal_handlers_disconnect_by_data(layout->clock, button);
        g_set_object(&layout->clock, clock);
        layout->frame_counter = -1;
        layout->frame_revision = layout->painted_revision = 0;
        g_signal_connect_object(clock, "before-paint", G_CALLBACK(trigger_frame_started), button, 0);
        g_signal_connect_object(clock, "after-paint", G_CALLBACK(trigger_frame_finished), button, 0);
    }
    return layout;
}
/* The parent natural frame discovers the new button before layout, rather
 * than depending on the 25 ms state reporter beating the first paint. */
static void prepare_trigger_frame(GtkWidget *widget, gpointer data) {
    GdkFrameClock *clock = data;
    if (GTK_IS_BUTTON(widget)
        && g_strcmp0(gtk_widget_get_name(widget), "typelay-menu-trigger") == 0) {
        watch_trigger_layout(widget);
        trigger_frame_started(clock, widget);
    }
    if (GTK_IS_CONTAINER(widget))
        gtk_container_forall(GTK_CONTAINER(widget), prepare_trigger_frame, clock);
}
static void prepare_language_frame(GdkFrameClock *clock, GtkWidget *window) {
    prepare_trigger_frame(window, clock);
}
static void report_language_control(GtkWidget *widget, gpointer data) {
    JsonBuilder *builder = data;
    const char *name = gtk_widget_get_name(widget);
    /* Public label discovery matches the existing reentry watcher. Report its
     * current allocation on every frame, never freeze a pre-layout center. */
    gboolean expand = GTK_IS_BUTTON(widget)
        && g_strcmp0(g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE"), "expand") == 0
        && g_strcmp0(gtk_button_get_label(GTK_BUTTON(widget)), "展开") == 0;
    if (expand) name = "fixture-expand";
    const char *stage = g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE");
    gboolean language_trigger = GTK_IS_BUTTON(widget) && g_strcmp0(name, "typelay-menu-trigger") == 0
        && stage && (g_str_equal(stage, "language-options") || g_str_has_prefix(stage, "language-retention-"));
    if (language_trigger && !g_object_get_data(G_OBJECT(widget), "fixture-trigger-pointer-watched")) {
        g_object_set_data(G_OBJECT(widget), "fixture-trigger-pointer-watched", GINT_TO_POINTER(1));
        /* Reuse the exact real GtkButton event-window crossing observer. */
        g_signal_connect(widget, "enter", G_CALLBACK(observe_expand_crossing), GINT_TO_POINTER(TRUE));
        g_signal_connect(widget, "leave", G_CALLBACK(observe_expand_crossing), GINT_TO_POINTER(FALSE));
        g_signal_connect(widget, "unmap", G_CALLBACK(observe_expand_unmapped), NULL);
    }
    GtkWidget *parent = gtk_widget_get_parent(widget);
    if (GTK_IS_LABEL(widget) && parent
        && g_strcmp0(gtk_widget_get_name(parent), "typelay-glossary-form") == 0)
        name = "fixture-glossary-feedback";
    if (expand || language_control(name) || g_str_has_prefix(name, "typelay-glossary-")
        || g_str_equal(name, "fixture-glossary-feedback")) {
        json_builder_set_member_name(builder, name);
        json_builder_begin_object(builder);
        if (g_strcmp0(name, "typelay-menu-trigger") == 0
            && g_strcmp0(g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE"), "language-options") == 0) {
            TriggerLayout *layout = watch_trigger_layout(widget);
            json_builder_set_member_name(builder, "labelRevision");
            json_builder_add_int_value(builder, layout->revision);
            json_builder_set_member_name(builder, "paintedLabelRevision");
            json_builder_add_int_value(builder, layout->painted_revision);
            json_builder_set_member_name(builder, "labelLayoutCurrent");
            json_builder_add_boolean_value(builder, layout->revision
                && layout->painted_revision == layout->revision
                && layout->clock == gtk_widget_get_frame_clock(widget)
                && layout->width == gtk_widget_get_allocated_width(widget)
                && layout->height == gtk_widget_get_allocated_height(widget));
        }
        if (expand || language_trigger) {
            json_builder_set_member_name(builder, "scale");
            json_builder_add_int_value(builder, gtk_widget_get_scale_factor(widget));
            json_builder_set_member_name(builder, "pointerInside");
            json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget)
                && g_object_get_data(G_OBJECT(widget), "fixture-expand-pointer-inside")
                && g_object_get_data(G_OBJECT(widget), "fixture-expand-enter-window")
                    == gtk_button_get_event_window(GTK_BUTTON(widget)));
            json_builder_set_member_name(builder, "pointerEnteredAtMs");
            json_builder_add_int_value(builder,
                (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-expand-enter-at"));
            json_builder_set_member_name(builder, "eventWindow");
            JsonNode *event_window = json_node_new(JSON_NODE_OBJECT);
            json_node_take_object(event_window, expand_event_window(widget));
            json_builder_add_value(builder, event_window);
        }
        json_builder_set_member_name(builder, "instance");
        json_builder_add_int_value(builder, language_instance(G_OBJECT(widget)));
        json_builder_set_member_name(builder, "visible");
        json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget));
        if (!expand && !language_trigger) {
            json_builder_set_member_name(builder, "pointerInside");
            json_builder_add_boolean_value(builder, (gtk_widget_get_state_flags(widget) & GTK_STATE_FLAG_PRELIGHT) != 0);
        }
        json_builder_set_member_name(builder, "enabled");
        json_builder_add_boolean_value(builder, gtk_widget_is_sensitive(widget));
        GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(report_window));
        json_builder_set_member_name(builder, "focused");
        json_builder_add_boolean_value(builder, focus && (focus == widget || gtk_widget_is_ancestor(focus, widget)));
        if (GTK_IS_BUTTON(widget)) {
            const char *label = gtk_button_get_label(GTK_BUTTON(widget));
            json_builder_set_member_name(builder, "text");
            json_builder_add_string_value(builder, label ? label : "");
        }
        if (GTK_IS_ENTRY(widget)) {
            json_builder_set_member_name(builder, "text");
            json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(widget)));
        } else if (GTK_IS_LABEL(widget)) {
            json_builder_set_member_name(builder, "text");
            json_builder_add_string_value(builder, gtk_label_get_text(GTK_LABEL(widget)));
        }
        gint x, y;
        if (gtk_widget_translate_coordinates(widget, report_window, 0, 0, &x, &y)) {
            json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
            json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
            json_builder_set_member_name(builder, "width"); json_builder_add_int_value(builder, gtk_widget_get_allocated_width(widget));
            json_builder_set_member_name(builder, "height"); json_builder_add_int_value(builder, gtk_widget_get_allocated_height(widget));
        }
        if (GTK_IS_MENU_BUTTON(widget)) {
            GtkPopover *popup = gtk_menu_button_get_popover(GTK_MENU_BUTTON(widget));
            GtkWidget *scroll = popup ? gtk_bin_get_child(GTK_BIN(popup)) : NULL;
            GtkWidget *list = scroll ? gtk_bin_get_child(GTK_BIN(scroll)) : NULL;
            if (GTK_IS_TREE_VIEW(list)) {
                watch_language_popup(widget, popup);
                json_builder_set_member_name(builder, "buttonFocused");
                json_builder_add_boolean_value(builder, focus == widget && gtk_widget_get_mapped(widget));
                json_builder_set_member_name(builder, "popupActive");
                json_builder_add_boolean_value(builder, gtk_toggle_button_get_active(GTK_TOGGLE_BUTTON(widget)));
                json_builder_set_member_name(builder, "popupShown");
                json_builder_add_boolean_value(builder, gtk_widget_get_visible(GTK_WIDGET(popup)));
                json_builder_set_member_name(builder, "popupFocused");
                json_builder_add_boolean_value(builder, focus && (focus == GTK_WIDGET(popup)
                    || gtk_widget_is_ancestor(focus, GTK_WIDGET(popup))));
                json_builder_set_member_name(builder, "listInstance");
                json_builder_add_int_value(builder, language_instance(G_OBJECT(list)));
                GtkTreeModel *model = gtk_tree_view_get_model(GTK_TREE_VIEW(list));
                json_builder_set_member_name(builder, "modelInstance");
                json_builder_add_int_value(builder, language_instance(G_OBJECT(model)));
                GtkTreeIter iter;
                gint selected = -1;
                if (gtk_tree_selection_get_selected(gtk_tree_view_get_selection(GTK_TREE_VIEW(list)), NULL, &iter)) {
                    GtkTreePath *path = gtk_tree_model_get_path(model, &iter);
                    selected = gtk_tree_path_get_indices(path)[0];
                    gtk_tree_path_free(path);
                }
                json_builder_set_member_name(builder, "selected");
                json_builder_add_int_value(builder, selected);
                json_builder_set_member_name(builder, "popupVisible");
                json_builder_add_boolean_value(builder, gtk_widget_get_mapped(GTK_WIDGET(popup)));
                json_builder_set_member_name(builder, "items");
                json_builder_begin_array(builder);
                if (model && gtk_tree_model_get_iter_first(model, &iter)) do {
                    char *label = NULL;
                    gtk_tree_model_get(model, &iter, 1, &label, -1);
                    json_builder_add_string_value(builder, label ? label : "");
                    g_free(label);
                } while (gtk_tree_model_iter_next(model, &iter));
                json_builder_end_array(builder);
            }
        }
        json_builder_end_object(builder);
    }
    if (GTK_IS_CONTAINER(widget)) gtk_container_forall(GTK_CONTAINER(widget), report_language_control, data);
}

#include "gtk_motion.inc"

#include "gtk_glossary.inc"

static gboolean report_state(gpointer unused) {
    (void)unused;
    if (report_closed) {
        report_source_id = 0;
        return G_SOURCE_REMOVE;
    }
    glossary_host_command();
    GtkWidget *active = other_window && gtk_window_is_active(GTK_WINDOW(other_window)) ? other_window : report_window;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(active));
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    glossary_report(builder);
    motion_report(builder);
    if (report_closed) goto closed;
    json_builder_set_member_name(builder, "observedAtMs");
    json_builder_add_int_value(builder, g_get_monotonic_time() / 1000);
    json_builder_set_member_name(builder, "first");
    json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(report_first)));
    json_builder_set_member_name(builder, "second");
    json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(report_second)));
    if (other_entry) {
        json_builder_set_member_name(builder, "other");
        json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(other_entry)));
    }
    json_builder_set_member_name(builder, "expandTransitions");
    json_builder_begin_array(builder);
    for (guint index = 0; index < expand_transition_count; index++) {
        JsonNode *transition = json_node_new(JSON_NODE_OBJECT);
        json_node_set_object(transition, expand_transitions[index]);
        json_builder_add_value(builder, transition);
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "languageTransitions");
    json_builder_begin_array(builder);
    for (guint index = 0; index < language_transition_count; index++) {
        JsonNode *transition = json_node_new(JSON_NODE_OBJECT);
        json_node_set_object(transition, language_transitions[index]);
        json_builder_add_value(builder, transition);
    }
    json_builder_end_array(builder);
    direct_control_report(builder);
    json_builder_set_member_name(builder, "focus");
    json_builder_add_string_value(builder, focus ? gtk_buildable_get_name(GTK_BUILDABLE(focus)) : "");
    if (g_getenv("TYPELAY_FIXTURE_COOPERATIVE")) {
        report_entry(builder, "firstEntry", report_first, &first_evidence);
        report_entry(builder, "secondEntry", report_second, &second_evidence);
        const char *names[] = {"firstInsertContext", "firstDeleteContext"};
        JsonObject *contexts[] = {first_insert_context, first_delete_context};
        for (guint i = 0; i < G_N_ELEMENTS(contexts); i++) {
            json_builder_set_member_name(builder, names[i]);
            if (contexts[i]) {
                JsonNode *change = json_node_new(JSON_NODE_OBJECT);
                json_node_set_object(change, contexts[i]);
                json_builder_add_value(builder, change);
            } else json_builder_add_null_value(builder);
        }
        TlFcitxStateV1 state = { .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
        gboolean valid = inspect_ime(report_first, &state);
        if (report_closed) goto closed;
        json_builder_set_member_name(builder, "cooperative");
        json_builder_add_boolean_value(builder, valid);
        json_builder_set_member_name(builder, "imeFocused");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_FOCUSED));
        json_builder_set_member_name(builder, "imeClear");
        json_builder_add_boolean_value(builder, valid && ime_clear(&state));
        json_builder_set_member_name(builder, "imeComposing");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_COMPOSING));
        state = (TlFcitxStateV1){ .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
        valid = inspect_ime(focus, &state);
        if (report_closed) goto closed;
        json_builder_set_member_name(builder, "focusedImeClear");
        json_builder_add_boolean_value(builder, valid && ime_clear(&state));
        json_builder_set_member_name(builder, "focusedImeComposing");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_COMPOSING));
        json_builder_set_member_name(builder, "fullscreen");
        json_builder_add_boolean_value(builder,
            (gdk_window_get_state(gtk_widget_get_window(report_window)) & GDK_WINDOW_STATE_FULLSCREEN) != 0);
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_width(report_window));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_height(report_window));
        json_builder_set_member_name(builder, "scale");
        json_builder_add_int_value(builder, gtk_widget_get_scale_factor(report_window));
        json_builder_set_member_name(builder, "overlayControls");
        json_builder_begin_object(builder);
        report_language_control(report_window, builder);
        json_builder_end_object(builder);
        GtkWidget *candidate = NULL;
        gtk_container_forall(GTK_CONTAINER(report_window), find_candidate, &candidate);
        gint x, y;
        json_builder_set_member_name(builder, "candidate");
        if (candidate && gtk_widget_get_mapped(candidate)
            && gtk_widget_translate_coordinates(candidate, report_window, 0, 0, &x, &y)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
            json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
            json_builder_set_member_name(builder, "width");
            json_builder_add_int_value(builder, gtk_widget_get_allocated_width(candidate));
            json_builder_set_member_name(builder, "height");
            json_builder_add_int_value(builder, gtk_widget_get_allocated_height(candidate));
            json_builder_set_member_name(builder, "editable");
            json_builder_add_boolean_value(builder, gtk_text_view_get_editable(GTK_TEXT_VIEW(candidate)));
            json_builder_set_member_name(builder, "focused");
            json_builder_add_boolean_value(builder, gtk_widget_has_focus(candidate));
            GdkRGBA foreground;
            gtk_style_context_get_color(gtk_widget_get_style_context(candidate),
                gtk_widget_get_state_flags(candidate), &foreground);
            char *color = gdk_rgba_to_string(&foreground);
            json_builder_set_member_name(builder, "foreground"); json_builder_add_string_value(builder, color);
            g_free(color);
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(candidate));
            GtkTextIter first, last;
            gtk_text_buffer_get_bounds(buffer, &first, &last);
            char *text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
            json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, text);
            g_free(text);
            json_builder_end_object(builder);
        } else json_builder_add_null_value(builder);
    }
    json_builder_set_member_name(builder, "userActionsStarted"); json_builder_add_int_value(builder, user_actions_started);
    json_builder_set_member_name(builder, "userActionsFinished"); json_builder_add_int_value(builder, user_actions_finished);
    json_builder_set_member_name(builder, "multiline");
    json_builder_begin_object(builder);
    GtkTextBuffer *multiline = gtk_text_view_get_buffer(GTK_TEXT_VIEW(report_multiline));
    GtkTextIter begin, end, selected_begin, selected_end;
    gtk_text_buffer_get_bounds(multiline, &begin, &end);
    char *body = gtk_text_buffer_get_text(multiline, &begin, &end, TRUE);
    json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, body);
    g_free(body);
    gtk_text_buffer_get_selection_bounds(multiline, &selected_begin, &selected_end);
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&selected_begin));
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&selected_end));
    gint x, y;
    if (gtk_widget_translate_coordinates(report_multiline, report_window, 0, 0, &x, &y)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
        json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
    }
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "excludedTextViews");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        if (gtk_widget_translate_coordinates(excluded_views[i], report_window, 0, 0, &x, &y)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name"); json_builder_add_string_value(builder, gtk_buildable_get_name(GTK_BUILDABLE(excluded_views[i])));
            json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
            json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
            json_builder_end_object(builder);
        }
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "functionKeys");
    json_builder_begin_array(builder);
    for (guint index = 0; index < G_N_ELEMENTS(function_down); index++) {
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "down"); json_builder_add_int_value(builder, function_down[index]);
        json_builder_set_member_name(builder, "up"); json_builder_add_int_value(builder, function_up[index]);
        json_builder_end_object(builder);
    }
    json_builder_end_array(builder);
    json_builder_end_object(builder);
    JsonNode *node = json_builder_get_root(builder);
    JsonGenerator *generator = json_generator_new();
    json_generator_set_root(generator, node);
    char *data = json_generator_to_data(generator, NULL);
    if (g_strcmp0(data, last_report) != 0) {
        g_file_set_contents(report_path, data, -1, NULL);
        g_free(last_report);
        last_report = g_strdup(data);
    }
    g_free(data);
    g_object_unref(generator);
    json_node_free(node);
    g_object_unref(builder);
    return G_SOURCE_CONTINUE;
closed:
    g_object_unref(builder);
    report_source_id = 0;
    return G_SOURCE_REMOVE;
}

/* An actual GTK client, not a production backend or a source-string assertion. */
int main(int argc, char **argv) {
    gtk_init(&argc, &argv);
    GtkWidget *window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_window_set_title(GTK_WINDOW(window), "TypeLay GTK contract fixture");
    gtk_window_set_default_size(GTK_WINDOW(window), 650, 480);
    GtkWidget *title = gtk_header_bar_new();
    gtk_header_bar_set_title(GTK_HEADER_BAR(title), "TypeLay input contracts");
    gtk_header_bar_set_show_close_button(GTK_HEADER_BAR(title), TRUE);
    gtk_window_set_titlebar(GTK_WINDOW(window), title);
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
    gtk_container_set_border_width(GTK_CONTAINER(box), 32);
    gtk_container_add(GTK_CONTAINER(window), box);
    GtkWidget *first = gtk_entry_new(), *second = gtk_search_entry_new(), *password = gtk_entry_new();
    gtk_buildable_set_name(GTK_BUILDABLE(first), "fixture-first");
    gtk_buildable_set_name(GTK_BUILDABLE(second), "fixture-second");
    gtk_buildable_set_name(GTK_BUILDABLE(password), "fixture-password");
    const char *initial = g_getenv("TYPELAY_FIXTURE_INITIAL_TEXT");
    gtk_entry_set_text(GTK_ENTRY(first), initial ? initial : "alpha 😀 omega");
    gtk_entry_set_text(GTK_ENTRY(second), "second field");
    gtk_entry_set_visibility(GTK_ENTRY(password), FALSE);
    gtk_entry_set_text(GTK_ENTRY(password), "fixture-only-private");
    gtk_box_pack_start(GTK_BOX(box), gtk_label_new("Ordinary entries and excluded password field"), FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(box), first, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(box), second, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(box), password, FALSE, FALSE, 0);
    GtkWidget *view = gtk_text_view_new();
    gtk_buildable_set_name(GTK_BUILDABLE(view), "fixture-multiline");
    gtk_text_buffer_set_text(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), "Before 😀\nPlease review this note.\nPlease send me the final version.\nAfter untouched", -1);
    gtk_widget_set_size_request(view, -1, 100);
    report_multiline = view;
    g_signal_connect(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), "begin-user-action", G_CALLBACK(action_started), NULL);
    g_signal_connect(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), "end-user-action", G_CALLBACK(action_finished), NULL);
    gtk_box_pack_start(GTK_BOX(box), view, FALSE, FALSE, 0);
    const char *names[] = {"fixture-tagged", "fixture-object", "fixture-readonly"};
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        GtkWidget *excluded = gtk_text_view_new();
        excluded_views[i] = excluded;
        gtk_buildable_set_name(GTK_BUILDABLE(excluded), names[i]);
        GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(excluded));
        gtk_text_buffer_set_text(buffer, "Excluded fixture content", -1);
        GtkTextIter first, last;
        gtk_text_buffer_get_bounds(buffer, &first, &last);
        if (i == 0) {
            GtkTextTag *tag = gtk_text_buffer_create_tag(buffer, "fixture-tag", "weight", PANGO_WEIGHT_BOLD, NULL);
            gtk_text_buffer_apply_tag(buffer, tag, &first, &last);
        } else if (i == 1) {
            GtkTextChildAnchor *anchor = gtk_text_buffer_create_child_anchor(buffer, &first);
            gtk_text_view_add_child_at_anchor(GTK_TEXT_VIEW(excluded), gtk_label_new("Embedded object"), anchor);
        } else gtk_text_view_set_editable(GTK_TEXT_VIEW(excluded), FALSE);
        gtk_widget_set_size_request(excluded, -1, 32);
        gtk_box_pack_start(GTK_BOX(box), excluded, FALSE, FALSE, 0);
    }

    g_signal_connect(window, "destroy", G_CALLBACK(fixture_destroyed), NULL);
    gtk_widget_show_all(window);
    if (g_getenv("TYPELAY_FIXTURE_FULLSCREEN")) gtk_window_fullscreen(GTK_WINDOW(window));
    gtk_widget_grab_focus(first);
    gtk_editable_set_position(GTK_EDITABLE(first), -1);
    if (g_getenv("TYPELAY_FIXTURE_TWO_WINDOWS")) {
        other_window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
        gtk_window_set_title(GTK_WINDOW(other_window), "TypeLay GTK second window");
        gtk_window_set_default_size(GTK_WINDOW(other_window), 600, 300);
        GtkWidget *other_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
        gtk_container_set_border_width(GTK_CONTAINER(other_box), 32);
        other_entry = gtk_entry_new();
        gtk_buildable_set_name(GTK_BUILDABLE(other_entry), "fixture-other-window");
        gtk_entry_set_text(GTK_ENTRY(other_entry), "The other window is unchanged.");
        gtk_box_pack_start(GTK_BOX(other_box), other_entry, FALSE, FALSE, 0);
        gtk_container_add(GTK_CONTAINER(other_window), other_box);
        g_signal_connect(other_window, "destroy", G_CALLBACK(fixture_destroyed), NULL);
        gtk_widget_show_all(other_window);
    }
    notification_fixture_init(view);
    direct_control_init();
    report_path = g_getenv("TYPELAY_FIXTURE_STATE");
    if (report_path && g_path_is_absolute(report_path)) {
        report_window = window;
        report_first = first;
        report_second = second;
        g_signal_connect_after(window, "set-focus", G_CALLBACK(language_focus_changed), NULL);
        g_signal_connect(window, "key-press-event", G_CALLBACK(observe_key), NULL);
        g_signal_connect(window, "key-release-event", G_CALLBACK(observe_key), NULL);
        watch_entry(first, &first_evidence);
        watch_entry(second, &second_evidence);
        motion_init();
        if (g_strcmp0(g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE"), "language-options") == 0) {
            GdkFrameClock *clock = gtk_widget_get_frame_clock(window);
            if (clock) g_signal_connect_object(clock, "before-paint",
                G_CALLBACK(prepare_language_frame), window, 0);
        }
        report_source_id = g_timeout_add(25, report_state, NULL);
    }
    gtk_main();
    g_free(last_report);
    g_free(motion_mode); g_free(motion_body);
    if (motion_frames) json_array_unref(motion_frames);
    for (guint index = 0; index < language_transition_count; index++)
        json_object_unref(language_transitions[index]);
    if (first_insert_context) json_object_unref(first_insert_context);
    if (first_delete_context) json_object_unref(first_delete_context);
    return 0;
}
