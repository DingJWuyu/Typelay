#include <gtk/gtk.h>
#include <pango/pangocairo.h>
#include <stdio.h>
#include <json-glib/json-glib.h>
#include "../../../src/linux/native/fcitx5/typelay-fcitx-v1.h"
#include "../support/fixture_state.h"
#include "gtk_font_observation.inc"

/* Only the isolated test executable writes this evidence. Production has no
 * command to dump source text, inject input or change a test model. */
static GtkWidget *report_window, *report_first, *report_second, *other_window, *other_entry;
static GtkWidget *protected_fields[2], *report_multiline, *excluded_views[3];
static const char *report_path;
static char *last_report;
static gint64 previous_report_timing[4];
typedef struct {
    guint insertions, deletions, focus_in, focus_out;
    gint64 focus_in_at_us;
} EntryEvidence;
static EntryEvidence first_evidence, second_evidence;
static JsonObject *first_insert_context, *first_delete_context;
#include "gtk_reentry.inc"

static guint function_down[24], function_up[24], tab_down, tab_up;
static JsonObject *focus_transitions[16];
static guint focus_sequence;
static void observe_focus(GObject *object, GParamSpec *spec, gpointer data) {
    (void)spec; (void)data;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(object));
    const char *scope = "other";
    for (GtkWidget *at = focus; at; at = gtk_widget_get_parent(at)) {
        if (at == report_first) { scope = "first"; break; }
        if (at == report_second) { scope = "second"; break; }
        if (g_strcmp0(gtk_widget_get_name(at), "typelay-overlay") == 0) { scope = "overlay"; break; }
    }
    guint index = focus_sequence++ % G_N_ELEMENTS(focus_transitions);
    if (focus_transitions[index]) json_object_unref(focus_transitions[index]);
    JsonObject *value = focus_transitions[index] = json_object_new();
    json_object_set_int_member(value, "sequence", focus_sequence);
    json_object_set_int_member(value, "atMonoMs", g_get_monotonic_time() / 1000);
    json_object_set_string_member(value, "type", focus ? G_OBJECT_TYPE_NAME(focus) : "none");
    json_object_set_string_member(value, "name", focus ? gtk_widget_get_name(focus) : "");
    json_object_set_string_member(value, "scope", scope);
    json_object_set_boolean_member(value, "canFocus", focus && gtk_widget_get_can_focus(focus));
    json_object_set_boolean_member(value, "focusable", focus && gtk_widget_get_focusable(focus));
}
static gboolean observe_key(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)controller; (void)data;
    GdkEventType type = gdk_event_get_event_type(event);
    if (type != GDK_KEY_PRESS && type != GDK_KEY_RELEASE) return FALSE;
    guint keyval = gdk_key_event_get_keyval(event);
    if (keyval == GDK_KEY_Tab || keyval == GDK_KEY_ISO_Left_Tab) {
        if (type == GDK_KEY_PRESS) tab_down++;
        else tab_up++;
    }
    if (keyval >= GDK_KEY_F1 && keyval <= GDK_KEY_F24) {
        guint index = keyval - GDK_KEY_F1;
        if (type == GDK_KEY_PRESS) function_down[index]++;
        else function_up[index]++;
    }
    return FALSE;
}
static GtkWidget *entry_client(GtkWidget *widget) {
    return GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(widget)));
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
    json_object_set_string_member(context, "secondText", gtk_editable_get_text(GTK_EDITABLE(report_second)));
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

static gboolean inspect_ime(GtkWidget *widget, TlFcitxStateV1 *state) {
    if (GTK_IS_ENTRY(widget)) widget = GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(widget)));
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
static guint language_instance(GObject *object);
/* Only the owned configuration fixture reads its two existing source widgets.
 * This does not install controllers, request paint or change their input state. */
static void report_configuration_source(JsonBuilder *builder, GtkWidget *widget) {
    const char *stage = g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE");
    if (!stage || (!g_str_equal(stage, "language-options")
        && !g_str_has_prefix(stage, "language-retention-")
        && !g_str_has_prefix(stage, "language-configuration-"))) return;
    json_builder_set_member_name(builder, "instance");
    json_builder_add_int_value(builder, language_instance(G_OBJECT(widget)));
    json_builder_set_member_name(builder, "topInstance");
    json_builder_add_int_value(builder, language_instance(G_OBJECT(report_window)));
    json_builder_set_member_name(builder, "topActive");
    json_builder_add_boolean_value(builder, gtk_window_is_active(GTK_WINDOW(report_window)));
    json_builder_set_member_name(builder, "mapped");
    json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget));
    json_builder_set_member_name(builder, "enabled");
    json_builder_add_boolean_value(builder, gtk_widget_is_sensitive(widget));
    GtkNative *native = GTK_NATIVE(report_window);
    GdkSurface *surface = gtk_native_get_surface(native);
    GdkSeat *seat = gdk_display_get_default_seat(gtk_widget_get_display(report_window));
    GdkDevice *pointer = seat ? gdk_seat_get_pointer(seat) : NULL;
    double x = 0, y = 0;
    GdkSurface *hit_surface = pointer ? gdk_device_get_surface_at_position(pointer, &x, &y) : NULL;
    json_builder_set_member_name(builder, "sourceHit");
    if (surface && hit_surface == surface && gtk_native_get_for_surface(surface) == native) {
        double ox, oy;
        gtk_native_get_surface_transform(native, &ox, &oy);
        x += ox; y += oy;
        GtkWidget *picked = gtk_widget_pick(report_window, x, y, GTK_PICK_DEFAULT);
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, x);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, y);
        json_builder_set_member_name(builder, "isSource");
        json_builder_add_boolean_value(builder, picked
            && (picked == widget || gtk_widget_is_ancestor(picked, widget)));
        json_builder_end_object(builder);
    } else json_builder_add_null_value(builder);
}
/* These are observations of the real widgets, never inputs to the provider. */
static void report_entry(JsonBuilder *builder, const char *name, GtkWidget *widget, const EntryEvidence *evidence) {
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(widget), &start, &end);
    json_builder_set_member_name(builder, name);
    json_builder_begin_object(builder);
    report_configuration_source(builder, widget);
    json_builder_set_member_name(builder, "focused");
    json_builder_add_boolean_value(builder, gtk_widget_has_focus(entry_client(widget)));
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, start);
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, end);
    json_builder_set_member_name(builder, "insertions"); json_builder_add_int_value(builder, evidence->insertions);
    json_builder_set_member_name(builder, "deletions"); json_builder_add_int_value(builder, evidence->deletions);
    json_builder_set_member_name(builder, "focusInAtUs"); json_builder_add_int_value(builder, evidence->focus_in_at_us);
    json_builder_set_member_name(builder, "focusIn"); json_builder_add_int_value(builder, evidence->focus_in);
    json_builder_set_member_name(builder, "focusOut"); json_builder_add_int_value(builder, evidence->focus_out);
    graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
    if (gtk_widget_compute_point(widget, report_window, &origin, &point)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_width(widget));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_height(widget));
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
        && g_strcmp0(gtk_widget_get_name(widget), "typelay-candidate") == 0) {
        *candidate = widget;
        remove_session_controller(widget, TRUE);
    } else for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child))
        find_candidate(child, data);
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
/* Candidate-only natural frame observation. Unlike ControlPaint below, this
 * never measures, queues allocation, requests a phase or begins clock updates.
 * The eight-entry ring describes public clock phases, not compositor pixels. */
typedef struct {
    GdkFrameClock *clock;
    GtkTextBuffer *buffer;
    gulong layout_handler, paint_handler, after_handler, changed_handler;
    gboolean mapped;
    guint64 map_generation, buffer_sequence, sequence;
    guint64 layout_map, paint_map, layout_buffer, paint_buffer;
    gint64 attached_at, layout_frame, paint_frame;
    JsonObject *frames[8];
} CandidateFrames;

static void candidate_frames_clear_clock(CandidateFrames *frames) {
    gulong handlers[] = {frames->layout_handler, frames->paint_handler, frames->after_handler};
    for (guint i = 0; i < G_N_ELEMENTS(handlers); i++)
        if (frames->clock && handlers[i] && g_signal_handler_is_connected(frames->clock, handlers[i]))
            g_signal_handler_disconnect(frames->clock, handlers[i]);
    frames->layout_handler = frames->paint_handler = frames->after_handler = 0;
    frames->layout_frame = frames->paint_frame = -1;
    g_clear_object(&frames->clock);
}
static void candidate_frames_free(gpointer data) {
    CandidateFrames *frames = data;
    candidate_frames_clear_clock(frames);
    if (frames->buffer && frames->changed_handler)
        g_signal_handler_disconnect(frames->buffer, frames->changed_handler);
    g_clear_object(&frames->buffer);
    for (guint i = 0; i < G_N_ELEMENTS(frames->frames); i++)
        if (frames->frames[i]) json_object_unref(frames->frames[i]);
    g_free(frames);
}
static gboolean candidate_frame_current(GtkWidget *widget, CandidateFrames *frames, GdkFrameClock *clock) {
    return frames && frames->clock == clock && gtk_widget_get_mapped(widget)
        && gtk_widget_get_frame_clock(widget) == clock
        && gtk_text_view_get_buffer(GTK_TEXT_VIEW(widget)) == frames->buffer;
}
static void candidate_natural_layout(GdkFrameClock *clock, gpointer data) {
    GtkWidget *widget = data;
    CandidateFrames *frames = g_object_get_data(G_OBJECT(widget), "fixture-candidate-natural-frames");
    if (!candidate_frame_current(widget, frames, clock)) return;
    frames->layout_frame = gdk_frame_clock_get_frame_counter(clock);
    frames->layout_map = frames->map_generation;
    frames->layout_buffer = frames->buffer_sequence;
}
static void candidate_natural_paint(GdkFrameClock *clock, gpointer data) {
    GtkWidget *widget = data;
    CandidateFrames *frames = g_object_get_data(G_OBJECT(widget), "fixture-candidate-natural-frames");
    if (!candidate_frame_current(widget, frames, clock)) return;
    frames->paint_frame = gdk_frame_clock_get_frame_counter(clock);
    frames->paint_map = frames->map_generation;
    frames->paint_buffer = frames->buffer_sequence;
}
/* Inspect only the existing scrollbar CSS/allocations in the original eight
 * natural frames. No measurement, resize, phase request or extra frame. */
static JsonArray *candidate_css_edges(const GtkBorder *edges) {
    JsonArray *values = json_array_new();
    json_array_add_int_element(values, edges->top);
    json_array_add_int_element(values, edges->right);
    json_array_add_int_element(values, edges->bottom);
    json_array_add_int_element(values, edges->left);
    return values;
}
static JsonObject *candidate_scrollbar_layout(GtkWidget *scroll) {
    JsonObject *value = json_object_new();
    GtkWidget *widget = gtk_scrolled_window_get_vscrollbar(GTK_SCROLLED_WINDOW(scroll));
    json_object_set_boolean_member(value, "overlayScrolling",
        gtk_scrolled_window_get_overlay_scrolling(GTK_SCROLLED_WINDOW(scroll)));
    JsonArray *nodes = json_array_new();
    json_object_set_array_member(value, "nodes", nodes);
    const char *names[] = {"scrollbar", "range", "trough", "slider"};
    for (guint index = 0; index < G_N_ELEMENTS(names); index++) {
        if (!widget || g_strcmp0(gtk_widget_get_css_name(widget), names[index]) != 0) {
            json_array_add_null_element(nodes);
            widget = NULL;
            continue;
        }
        JsonObject *node = json_object_new();
        json_object_set_string_member(node, "node", names[index]);
        json_object_set_int_member(node, "instance", language_instance(G_OBJECT(widget)));
        json_object_set_boolean_member(node, "mapped", gtk_widget_get_mapped(widget));
        json_object_set_boolean_member(node, "childVisible", gtk_widget_get_child_visible(widget));
        json_object_set_int_member(node, "state", gtk_widget_get_state_flags(widget));
        json_object_set_int_member(node, "width", gtk_widget_get_width(widget));
        json_object_set_int_member(node, "height", gtk_widget_get_height(widget));
        json_object_set_boolean_member(node, "overlayIndicator", gtk_widget_has_css_class(widget, "overlay-indicator"));
        json_object_set_boolean_member(node, "hovering", gtk_widget_has_css_class(widget, "hovering"));
        json_object_set_boolean_member(node, "dragging", gtk_widget_has_css_class(widget, "dragging"));
        GtkBorder border, padding, margin;
        G_GNUC_BEGIN_IGNORE_DEPRECATIONS
        GtkStyleContext *style = gtk_widget_get_style_context(widget);
        gtk_style_context_get_border(style, &border);
        gtk_style_context_get_padding(style, &padding);
        gtk_style_context_get_margin(style, &margin);
        G_GNUC_END_IGNORE_DEPRECATIONS
        json_object_set_array_member(node, "borderTRBL", candidate_css_edges(&border));
        json_object_set_array_member(node, "paddingTRBL", candidate_css_edges(&padding));
        json_object_set_array_member(node, "marginTRBL", candidate_css_edges(&margin));
        json_array_add_object_element(nodes, node);
        widget = gtk_widget_get_first_child(widget);
    }
    return value;
}
static void candidate_natural_after_paint(GdkFrameClock *clock, gpointer data) {
    GtkWidget *widget = data;
    CandidateFrames *frames = g_object_get_data(G_OBJECT(widget), "fixture-candidate-natural-frames");
    if (!candidate_frame_current(widget, frames, clock)) return;
    gint64 frame = gdk_frame_clock_get_frame_counter(clock);
    if (frame < 0 || frames->layout_frame != frame || frames->paint_frame != frame
        || frames->layout_map != frames->map_generation || frames->paint_map != frames->map_generation
        || frames->layout_buffer != frames->buffer_sequence || frames->paint_buffer != frames->buffer_sequence) return;
    guint index = frames->sequence++ % G_N_ELEMENTS(frames->frames);
    if (frames->frames[index]) json_object_unref(frames->frames[index]);
    JsonObject *value = frames->frames[index] = json_object_new();
    json_object_set_int_member(value, "sequence", frames->sequence);
    json_object_set_int_member(value, "atMonoMs", g_get_monotonic_time() / 1000);
    json_object_set_int_member(value, "frame", frame);
    json_object_set_int_member(value, "clock", language_instance(G_OBJECT(clock)));
    json_object_set_int_member(value, "candidate", language_instance(G_OBJECT(widget)));
    json_object_set_int_member(value, "buffer", language_instance(G_OBJECT(frames->buffer)));
    json_object_set_int_member(value, "mapGeneration", frames->map_generation);
    json_object_set_int_member(value, "bufferSequence", frames->buffer_sequence);
    json_object_set_int_member(value, "width", gtk_widget_get_width(widget));
    json_object_set_int_member(value, "height", gtk_widget_get_height(widget));
    GtkWidget *scroll = gtk_widget_get_ancestor(widget, GTK_TYPE_SCROLLED_WINDOW);
    if (scroll) {
        gint requested_width, requested_height;
        gtk_widget_get_size_request(scroll, &requested_width, &requested_height);
        json_object_set_int_member(value, "scrollHeight", gtk_widget_get_height(scroll));
        json_object_set_int_member(value, "scrollRequestedHeight", requested_height);
        json_object_set_object_member(value, "scrollbarLayout", candidate_scrollbar_layout(scroll));
    }
    GdkRGBA foreground;
#if GTK_CHECK_VERSION(4, 10, 0)
    gtk_widget_get_color(widget, &foreground);
#else
    gtk_style_context_get_color(gtk_widget_get_style_context(widget), &foreground);
#endif
    char *color = gdk_rgba_to_string(&foreground);
    json_object_set_string_member(value, "foreground", color);
    g_free(color);
    GtkTextIter start, end;
    gtk_text_buffer_get_bounds(frames->buffer, &start, &end);
    char *text = gtk_text_buffer_get_text(frames->buffer, &start, &end, TRUE);
    char *digest = g_compute_checksum_for_string(G_CHECKSUM_SHA256, text, -1);
    json_object_set_string_member(value, "textSha256", digest);
    json_object_set_int_member(value, "textCharacters", gtk_text_buffer_get_char_count(frames->buffer));
    g_free(digest); g_free(text);
}
static void candidate_buffer_changed(GtkTextBuffer *buffer, gpointer data) {
    CandidateFrames *frames = data;
    if (frames->buffer == buffer) frames->buffer_sequence++;
}
static void candidate_frames_sync(GtkWidget *widget) {
    CandidateFrames *frames = g_object_get_data(G_OBJECT(widget), "fixture-candidate-natural-frames");
    if (!frames) return;
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(widget));
    if (buffer != frames->buffer) {
        if (frames->buffer && frames->changed_handler)
            g_signal_handler_disconnect(frames->buffer, frames->changed_handler);
        g_set_object(&frames->buffer, buffer);
        frames->changed_handler = g_signal_connect(buffer, "changed", G_CALLBACK(candidate_buffer_changed), frames);
        frames->buffer_sequence++;
        frames->layout_frame = frames->paint_frame = -1;
    }
    gboolean mapped = gtk_widget_get_mapped(widget);
    if (mapped != frames->mapped) {
        frames->mapped = mapped;
        frames->map_generation++;
        frames->layout_frame = frames->paint_frame = -1;
    }
    GdkFrameClock *clock = mapped ? gtk_widget_get_frame_clock(widget) : NULL;
    if (clock == frames->clock) return;
    candidate_frames_clear_clock(frames);
    if (!clock) return;
    frames->clock = g_object_ref(clock);
    frames->attached_at = g_get_monotonic_time() / 1000;
    frames->layout_handler = g_signal_connect_after(clock, "layout", G_CALLBACK(candidate_natural_layout), widget);
    frames->paint_handler = g_signal_connect_after(clock, "paint", G_CALLBACK(candidate_natural_paint), widget);
    frames->after_handler = g_signal_connect_after(clock, "after-paint", G_CALLBACK(candidate_natural_after_paint), widget);
}
static void candidate_mapped(GtkWidget *widget, gpointer unused) {
    (void)unused;
    candidate_frames_sync(widget);
}
static void candidate_buffer_replaced(GObject *object, GParamSpec *property, gpointer unused) {
    (void)property; (void)unused;
    candidate_frames_sync(GTK_WIDGET(object));
}
static void report_candidate_natural_frames(JsonBuilder *builder, GtkWidget *widget) {
    CandidateFrames *frames = g_object_get_data(G_OBJECT(widget), "fixture-candidate-natural-frames");
    if (!frames) {
        frames = g_new0(CandidateFrames, 1);
        frames->layout_frame = frames->paint_frame = -1;
        g_object_set_data_full(G_OBJECT(widget), "fixture-candidate-natural-frames", frames, candidate_frames_free);
        g_signal_connect_after(widget, "map", G_CALLBACK(candidate_mapped), NULL);
        g_signal_connect_after(widget, "unmap", G_CALLBACK(candidate_mapped), NULL);
        g_signal_connect(widget, "notify::buffer", G_CALLBACK(candidate_buffer_replaced), NULL);
    }
    candidate_frames_sync(widget);
    json_builder_set_member_name(builder, "naturalFrames");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "candidate"); json_builder_add_int_value(builder, language_instance(G_OBJECT(widget)));
    json_builder_set_member_name(builder, "buffer"); json_builder_add_int_value(builder, language_instance(G_OBJECT(frames->buffer)));
    json_builder_set_member_name(builder, "clock"); json_builder_add_int_value(builder, language_instance(G_OBJECT(frames->clock)));
    json_builder_set_member_name(builder, "attachedAtMs"); json_builder_add_int_value(builder, frames->attached_at);
    json_builder_set_member_name(builder, "mapGeneration"); json_builder_add_int_value(builder, frames->map_generation);
    json_builder_set_member_name(builder, "bufferSequence"); json_builder_add_int_value(builder, frames->buffer_sequence);
    json_builder_set_member_name(builder, "layoutFrame"); json_builder_add_int_value(builder, frames->layout_frame);
    json_builder_set_member_name(builder, "paintFrame"); json_builder_add_int_value(builder, frames->paint_frame);
    json_builder_set_member_name(builder, "completedCount"); json_builder_add_int_value(builder, frames->sequence);
    json_builder_set_member_name(builder, "frames"); json_builder_begin_array(builder);
    guint64 first = frames->sequence > G_N_ELEMENTS(frames->frames) ? frames->sequence - G_N_ELEMENTS(frames->frames) : 0;
    for (guint64 i = first; i < frames->sequence; i++) {
        JsonNode *node = json_node_new(JSON_NODE_OBJECT);
        json_node_set_object(node, frames->frames[i % G_N_ELEMENTS(frames->frames)]);
        json_builder_add_value(builder, node);
    }
    json_builder_end_array(builder);
    json_builder_end_object(builder);
}

/* Bounded diagnostic for the first language popup transitions. Signals only
 * sample public state: no measure, focus operation or event-loop iteration. */
static JsonObject *language_transitions[16];
static guint language_transition_count;
static void observe_language_popup(GtkWidget *button, const char *event) {
    if (language_transition_count == G_N_ELEMENTS(language_transitions)) return;
    GtkPopover *popup = gtk_menu_button_get_popover(GTK_MENU_BUTTON(button));
    if (!popup) return;
    JsonObject *value = language_transitions[language_transition_count++] = json_object_new();
    json_object_set_int_member(value, "atMonoMs", g_get_monotonic_time() / 1000);
    json_object_set_string_member(value, "control", gtk_widget_get_name(button));
    json_object_set_string_member(value, "event", event);
    json_object_set_boolean_member(value, "active", gtk_menu_button_get_active(GTK_MENU_BUTTON(button)));
    json_object_set_boolean_member(value, "visible", gtk_widget_get_visible(GTK_WIDGET(popup)));
    json_object_set_boolean_member(value, "mapped", gtk_widget_get_mapped(GTK_WIDGET(popup)));
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
    g_object_set_data(G_OBJECT(button), "fixture-language-popup-watched", GINT_TO_POINTER(1));
    g_signal_connect_after(button, "notify::active", G_CALLBACK(language_active_changed), NULL);
    g_signal_connect_object(popup, "notify::visible", G_CALLBACK(language_visible_changed), button, G_CONNECT_AFTER);
    g_signal_connect_object(popup, "map", G_CALLBACK(language_mapped), button, G_CONNECT_AFTER);
    g_signal_connect_object(popup, "unmap", G_CALLBACK(language_unmapped), button, G_CONNECT_AFTER);
}
static gboolean language_control(const char *name) {
    return g_strcmp0(name, "typelay-menu-trigger") == 0
        || g_str_has_prefix(name, "typelay-more")
        || g_strcmp0(name, "typelay-regenerate") == 0 || g_strcmp0(name, "typelay-copy") == 0
        || g_strcmp0(name, "typelay-hide") == 0 || g_strcmp0(name, "typelay-exclude") == 0
        || g_strcmp0(name, "typelay-settings") == 0 || g_strcmp0(name, "typelay-alternative") == 0
        || g_strcmp0(name, "typelay-source-language") == 0
        || g_strcmp0(name, "typelay-target-language") == 0
        || g_strcmp0(name, "typelay-apply-languages") == 0
        || g_str_has_prefix(name, "typelay-style-")
        || g_strcmp0(name, "typelay-language-menu") == 0
        || g_str_has_prefix(name, "typelay-language-configuration")
        || g_str_has_prefix(name, "typelay-language-choice-")
        || g_strcmp0(name, "typelay-language-search") == 0
        || g_strcmp0(name, "typelay-language-swap") == 0
        || g_str_has_prefix(name, "typelay-glossary-");
}
typedef struct {
    guint signal_count;
    const char *last_signal;
    gint64 observed_at;
    gint event_type;
    guint64 surface_instance, native_instance, widget_instance, controller_instance;
    double x, y;
    gboolean has_position, pointer_device;
} ControlPointerEvent;
static void observe_control_pointer_event(GtkEventControllerMotion *motion, const char *signal,
        gboolean has_position, double x, double y) {
    GtkEventController *controller = GTK_EVENT_CONTROLLER(motion);
    ControlPointerEvent *observation = g_object_get_data(G_OBJECT(controller), "fixture-last-pointer-event");
    if (!observation) {
        observation = g_new0(ControlPointerEvent, 1);
        g_object_set_data_full(G_OBJECT(controller), "fixture-last-pointer-event", observation, g_free);
    }
    if (observation->signal_count < G_MAXUINT) observation->signal_count++;
    observation->last_signal = signal;
    observation->observed_at = g_get_monotonic_time() / 1000;
    /* get_current_event is meaningful only inside this actual dispatch.
     * Store scalars, never keep an event, surface, or widget alive. */
    GdkEvent *event = gtk_event_controller_get_current_event(controller);
    GdkSurface *surface = event ? gdk_event_get_surface(event) : NULL;
    GtkNative *native = surface ? gtk_native_get_for_surface(surface) : NULL;
    gboolean owned = native && gtk_widget_get_root(GTK_WIDGET(native)) == GTK_ROOT(report_window);
    observation->event_type = event ? (gint)gdk_event_get_event_type(event) : -1;
    observation->surface_instance = owned ? language_instance(G_OBJECT(surface)) : 0;
    observation->native_instance = owned ? language_instance(G_OBJECT(native)) : 0;
    observation->widget_instance = language_instance(G_OBJECT(gtk_event_controller_get_widget(controller)));
    observation->controller_instance = language_instance(G_OBJECT(controller));
    observation->has_position = has_position;
    observation->x = x; observation->y = y;
    GdkSeat *seat = gdk_display_get_default_seat(gtk_widget_get_display(GTK_WIDGET(report_window)));
    observation->pointer_device = event && seat
        && gdk_event_get_device(event) == gdk_seat_get_pointer(seat);
}
static void control_pointer_motion(GtkEventControllerMotion *motion, double x, double y, gpointer data) {
    (void)x; (void)y; (void)data;
    observe_control_pointer_event(motion, "motion", TRUE, x, y);
}
static void report_control_pointer_event(JsonBuilder *builder, GtkEventControllerMotion *motion) {
    GtkEventController *controller = GTK_EVENT_CONTROLLER(motion);
    ControlPointerEvent *observation = g_object_get_data(G_OBJECT(controller), "fixture-last-pointer-event");
    GtkNative *native = gtk_widget_get_native(gtk_event_controller_get_widget(controller));
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "controllerInstance");
    json_builder_add_int_value(builder, language_instance(G_OBJECT(controller)));
    json_builder_set_member_name(builder, "widgetInstance");
    json_builder_add_int_value(builder, language_instance(G_OBJECT(gtk_event_controller_get_widget(controller))));
    json_builder_set_member_name(builder, "eventControllerInstance");
    json_builder_add_int_value(builder, observation ? observation->controller_instance : 0);
    json_builder_set_member_name(builder, "eventWidgetInstance");
    json_builder_add_int_value(builder, observation ? observation->widget_instance : 0);
    json_builder_set_member_name(builder, "hasPosition");
    json_builder_add_boolean_value(builder, observation && observation->has_position);
    json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, observation ? observation->x : 0);
    json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, observation ? observation->y : 0);
    json_builder_set_member_name(builder, "pointerDevice");
    json_builder_add_boolean_value(builder, observation && observation->pointer_device);
    json_builder_set_member_name(builder, "phase");
    json_builder_add_int_value(builder, gtk_event_controller_get_propagation_phase(controller));
    json_builder_set_member_name(builder, "limit");
    json_builder_add_int_value(builder, gtk_event_controller_get_propagation_limit(controller));
    json_builder_set_member_name(builder, "nativeInstance");
    json_builder_add_int_value(builder, native ? language_instance(G_OBJECT(native)) : 0);
    json_builder_set_member_name(builder, "signalCount");
    json_builder_add_int_value(builder, observation ? observation->signal_count : 0);
    json_builder_set_member_name(builder, "lastSignal");
    if (observation) json_builder_add_string_value(builder, observation->last_signal);
    else json_builder_add_null_value(builder);
    json_builder_set_member_name(builder, "observedAtMonoMs");
    json_builder_add_int_value(builder, observation ? observation->observed_at : 0);
    json_builder_set_member_name(builder, "eventType");
    json_builder_add_int_value(builder, observation ? observation->event_type : -1);
    json_builder_set_member_name(builder, "eventSurfaceInstance");
    json_builder_add_int_value(builder, observation ? observation->surface_instance : 0);
    json_builder_set_member_name(builder, "eventNativeInstance");
    json_builder_add_int_value(builder, observation ? observation->native_instance : 0);
    json_builder_end_object(builder);
}
static void control_pointer_enter(GtkEventControllerMotion *motion, double x, double y, gpointer data) {
    (void)x; (void)y; (void)data;
    observe_control_pointer_event(motion, "enter", TRUE, x, y);
    GtkWidget *widget = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(motion));
    g_object_set_data(G_OBJECT(widget), "fixture-control-enter-at",
        (gpointer)(gintptr)(g_get_monotonic_time() / 1000));
}
static void control_pointer_leave(GtkEventControllerMotion *motion, gpointer data) {
    (void)data;
    observe_control_pointer_event(motion, "leave", FALSE, 0, 0);
    GtkWidget *widget = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(motion));
    g_object_set_data(G_OBJECT(widget), "fixture-control-enter-at", NULL);
    g_object_set_data(G_OBJECT(widget), "fixture-control-leave-at",
        (gpointer)(gintptr)(g_get_monotonic_time() / 1000));
}
static void control_pointer_unmap(GtkWidget *widget, gpointer data) {
    (void)data;
    g_object_set_data(G_OBJECT(widget), "fixture-control-enter-at", NULL);
}
/* Independent phase observations support the fixed-point click contract.
 * These void signal observers do not consume pointer events or move focus. */
static void control_capture_pointer_enter(GtkEventControllerMotion *motion, double x, double y, gpointer data) {
    (void)x; (void)y; (void)data;
    observe_control_pointer_event(motion, "enter", TRUE, x, y);
    GtkWidget *widget = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(motion));
    g_object_set_data(G_OBJECT(widget), "fixture-control-capture-enter-at",
        (gpointer)(gintptr)(g_get_monotonic_time() / 1000));
}
static void control_capture_pointer_leave(GtkEventControllerMotion *motion, gpointer data) {
    (void)data;
    observe_control_pointer_event(motion, "leave", FALSE, 0, 0);
    GtkWidget *widget = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(motion));
    g_object_set_data(G_OBJECT(widget), "fixture-control-capture-enter-at", NULL);
    g_object_set_data(G_OBJECT(widget), "fixture-control-capture-leave-at",
        (gpointer)(gintptr)(g_get_monotonic_time() / 1000));
}
static void control_capture_pointer_unmap(GtkWidget *widget, gpointer data) {
    (void)data;
    g_object_set_data(G_OBJECT(widget), "fixture-control-capture-enter-at", NULL);
}
static JsonNode *control_surface_geometry(GtkWidget *widget) {
    GtkNative *native = gtk_widget_get_native(widget);
    GdkSurface *surface = native ? gtk_native_get_surface(native) : NULL;
    GdkSurface *parent = gtk_native_get_surface(GTK_NATIVE(report_window));
    if (!gtk_widget_get_mapped(widget)) return NULL;
    graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
    double tx = 0, ty = 0;
    if (!surface || !parent || !gdk_surface_get_mapped(surface) || !gdk_surface_get_mapped(parent)
        || !gtk_widget_compute_point(widget, GTK_WIDGET(native), &origin, &point)) {
        return NULL;
    }
    /* GtkNative's transform is surface -> widget; invert before translating
     * the real popup surface to its parent. Widget-tree offsets are not a
     * substitute for the compositor-configured popup position. */
    gtk_native_get_surface_transform(native, &tx, &ty);
    double x = (double)point.x - tx, y = (double)point.y - ty;
    if (!gdk_surface_translate_coordinates(surface, parent, &x, &y)) {
        return NULL;
    }
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, x);
    json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, y);
    json_builder_set_member_name(builder, "surfaceInstance");
    json_builder_add_int_value(builder, language_instance(G_OBJECT(surface)));
    json_builder_set_member_name(builder, "scale");
    json_builder_add_int_value(builder, gdk_surface_get_scale_factor(surface));
    json_builder_set_member_name(builder, "nativeX"); json_builder_add_double_value(builder, point.x);
    json_builder_set_member_name(builder, "nativeY"); json_builder_add_double_value(builder, point.y);
    json_builder_set_member_name(builder, "transformX"); json_builder_add_double_value(builder, tx);
    json_builder_set_member_name(builder, "transformY"); json_builder_add_double_value(builder, ty);
    json_builder_set_member_name(builder, "popup"); json_builder_add_boolean_value(builder, GDK_IS_POPUP(surface));
    if (GDK_IS_POPUP(surface)) {
        json_builder_set_member_name(builder, "popupX");
        json_builder_add_int_value(builder, gdk_popup_get_position_x(GDK_POPUP(surface)));
        json_builder_set_member_name(builder, "popupY");
        json_builder_add_int_value(builder, gdk_popup_get_position_y(GDK_POPUP(surface)));
    }
    json_builder_set_member_name(builder, "width"); json_builder_add_int_value(builder, gtk_widget_get_width(widget));
    json_builder_set_member_name(builder, "height"); json_builder_add_int_value(builder, gtk_widget_get_height(widget));
    json_builder_set_member_name(builder, "instance"); json_builder_add_int_value(builder, language_instance(G_OBJECT(widget)));
    json_builder_end_object(builder);
    JsonNode *result = json_builder_get_root(builder);
    g_object_unref(builder);
    return result;
}

typedef struct {
    GdkFrameClock *clock;
    gulong handler, layout_handler, paint_handler;
    guint request_source;
    guint64 map_generation, painted_map_generation, surface_instance;
    gint64 mapped_at, requested_frame, layout_frame, paint_frame, painted_frame, painted_at;
    gint64 completed_layout_frame, completed_paint_frame;
    JsonNode *geometry;
    char *label;
    guint64 label_revision, painted_label_revision;
    JsonObject *last_map, *last_unmap;
} ControlPaint;

/* Only the owned preview trigger of the configuration fixture. Two fixed
 * records reuse its existing map/unmap callbacks; no measure, phase request or
 * extra callback is installed. Explicit size requests are NOT CSS minima. */
static void control_map_observation(GtkWidget *widget, ControlPaint *paint, gboolean mapped) {
    const char *stage = g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE");
    if (!stage || !(g_str_equal(stage, "language-options")
            || g_str_has_prefix(stage, "language-retention-")
            || g_str_has_prefix(stage, "language-configuration-"))
        || g_strcmp0(gtk_widget_get_name(widget), "typelay-menu-trigger") != 0) return;
    GtkNative *native = gtk_widget_get_native(widget);
    if (!GTK_IS_POPOVER(native)
        || g_strcmp0(gtk_widget_get_name(GTK_WIDGET(native)), "typelay-overlay") != 0) return;
    GtkWidget *popup = GTK_WIDGET(native), *card = gtk_popover_get_child(GTK_POPOVER(native));
    GtkWidget *scroll = NULL;
    GtkWidget *child = card ? gtk_widget_get_first_child(card) : NULL;
    for (guint count = 0; child && count < 4; count++, child = gtk_widget_get_next_sibling(child)) {
        if (GTK_IS_OVERLAY(child) && g_strcmp0(gtk_widget_get_name(child), "typelay-body") == 0) {
            scroll = gtk_overlay_get_child(GTK_OVERLAY(child));
            break;
        }
    }
    GdkSurface *surface = gtk_native_get_surface(native);
    GdkFrameClock *clock = paint->clock ? paint->clock : gtk_widget_get_frame_clock(widget);
    GSignalInvocationHint *hint = clock ? g_signal_get_invocation_hint(clock) : NULL;
    JsonObject *value = json_object_new();
    json_object_set_int_member(value, "atMonoMs", g_get_monotonic_time() / 1000);
    json_object_set_int_member(value, "mapGeneration", paint->map_generation);
    json_object_set_int_member(value, "control", language_instance(G_OBJECT(widget)));
    json_object_set_int_member(value, "native", language_instance(G_OBJECT(native)));
    json_object_set_int_member(value, "surface", language_instance(G_OBJECT(surface)));
    json_object_set_int_member(value, "clock", language_instance(G_OBJECT(clock)));
    json_object_set_int_member(value, "frame", clock ? gdk_frame_clock_get_frame_counter(clock) : -1);
    json_object_set_int_member(value, "observedLayoutFrame", paint->layout_frame);
    json_object_set_int_member(value, "observedPaintFrame", paint->paint_frame);
    json_object_set_boolean_member(value, "insideClockLayout", hint
        && hint->signal_id == g_signal_lookup("layout", GDK_TYPE_FRAME_CLOCK));
    json_object_set_boolean_member(value, "controlMapped", gtk_widget_get_mapped(widget));
    json_object_set_boolean_member(value, "controlVisible", gtk_widget_get_visible(widget));
    json_object_set_boolean_member(value, "popupMapped", gtk_widget_get_mapped(popup));
    json_object_set_boolean_member(value, "popupVisible", gtk_widget_get_visible(popup));
    json_object_set_boolean_member(value, "surfaceMapped", surface && gdk_surface_get_mapped(surface));
    json_object_set_int_member(value, "surfaceWidth", surface ? gdk_surface_get_width(surface) : 0);
    json_object_set_int_member(value, "surfaceHeight", surface ? gdk_surface_get_height(surface) : 0);
    GtkWidget *widgets[] = {popup, card, scroll};
    const char *names[] = {"popup", "card", "scroll"};
    for (guint i = 0; i < G_N_ELEMENTS(widgets); i++) {
        JsonObject *item = json_object_new();
        json_object_set_boolean_member(item, "present", widgets[i] != NULL);
        if (widgets[i]) {
            gint width, height;
            graphene_rect_t bounds;
            gtk_widget_get_size_request(widgets[i], &width, &height);
            json_object_set_int_member(item, "requestedWidth", width);
            json_object_set_int_member(item, "requestedHeight", height);
            /* Border bounds in this widget's own coordinates, not the former
             * margin allocation or a CSS minimum; no layout is requested. */
            if (gtk_widget_compute_bounds(widgets[i], widgets[i], &bounds)) {
                json_object_set_double_member(item, "boundsWidth", bounds.size.width);
                json_object_set_double_member(item, "boundsHeight", bounds.size.height);
            } else {
                json_object_set_null_member(item, "boundsWidth");
                json_object_set_null_member(item, "boundsHeight");
            }
        }
        json_object_set_object_member(value, names[i], item);
    }
    JsonObject **slot = mapped ? &paint->last_map : &paint->last_unmap;
    g_clear_pointer(slot, json_object_unref);
    *slot = value;
}

static void control_paint_clear(ControlPaint *paint) {
    if (paint->request_source) {
        g_source_remove(paint->request_source);
        paint->request_source = 0;
    }
    gulong handlers[] = {paint->handler, paint->layout_handler, paint->paint_handler};
    for (guint i = 0; i < G_N_ELEMENTS(handlers); i++)
        if (paint->clock && handlers[i] && g_signal_handler_is_connected(paint->clock, handlers[i]))
            g_signal_handler_disconnect(paint->clock, handlers[i]);
    paint->handler = paint->layout_handler = paint->paint_handler = 0;
    paint->layout_frame = paint->paint_frame = -1;
    g_clear_object(&paint->clock);
    g_clear_pointer(&paint->geometry, json_node_unref);
    paint->painted_at = 0;
    paint->painted_label_revision = 0;
    g_clear_pointer(&paint->label, g_free);
}
static void control_paint_free(gpointer data) {
    ControlPaint *paint = data;
    control_paint_clear(paint);
    g_clear_pointer(&paint->last_map, json_object_unref);
    g_clear_pointer(&paint->last_unmap, json_object_unref);
    g_free(paint);
}
static void control_layout(GdkFrameClock *clock, gpointer data) {
    GtkWidget *widget = data;
    ControlPaint *paint = g_object_get_data(G_OBJECT(widget), "fixture-control-paint");
    if (paint && paint->clock == clock && gtk_widget_get_mapped(widget))
        paint->layout_frame = gdk_frame_clock_get_frame_counter(clock);
}
static void control_paint(GdkFrameClock *clock, gpointer data) {
    GtkWidget *widget = data;
    ControlPaint *paint = g_object_get_data(G_OBJECT(widget), "fixture-control-paint");
    if (paint && paint->clock == clock && gtk_widget_get_mapped(widget))
        paint->paint_frame = gdk_frame_clock_get_frame_counter(clock);
}
static void control_after_paint(GdkFrameClock *clock, gpointer data) {
    GtkWidget *widget = data;
    ControlPaint *paint = g_object_get_data(G_OBJECT(widget), "fixture-control-paint");
    if (!paint || paint->request_source || paint->clock != clock || !gtk_widget_get_mapped(widget)
        || gtk_widget_get_frame_clock(widget) != clock
        || gdk_frame_clock_get_frame_counter(clock) <= paint->requested_frame
        || paint->layout_frame != gdk_frame_clock_get_frame_counter(clock)
        || paint->paint_frame != paint->layout_frame) return;
    g_clear_pointer(&paint->geometry, json_node_unref);
    paint->geometry = control_surface_geometry(widget);
    paint->completed_layout_frame = paint->layout_frame;
    paint->completed_paint_frame = paint->paint_frame;
    paint->painted_map_generation = paint->map_generation;
    paint->painted_frame = gdk_frame_clock_get_frame_counter(clock);
    paint->painted_at = g_get_monotonic_time() / 1000;
    paint->painted_label_revision = paint->label_revision;
}
static gboolean control_request_frame(gpointer data) {
    GtkWidget *widget = data;
    ControlPaint *paint = g_object_get_data(G_OBJECT(widget), "fixture-control-paint");
    paint->request_source = 0;
    if (paint->clock && gtk_widget_get_mapped(widget)) {
        paint->requested_frame = gdk_frame_clock_get_frame_counter(paint->clock);
        gdk_frame_clock_request_phase(paint->clock, GDK_FRAME_CLOCK_PHASE_LAYOUT
            | GDK_FRAME_CLOCK_PHASE_PAINT | GDK_FRAME_CLOCK_PHASE_AFTER_PAINT);
    }
    return G_SOURCE_REMOVE;
}
static void control_label_changed(GObject *object, GParamSpec *property, gpointer unused) {
    (void)property; (void)unused;
    GtkWidget *widget = GTK_WIDGET(object);
    ControlPaint *paint = g_object_get_data(object, "fixture-control-paint");
    const char *label = gtk_button_get_label(GTK_BUTTON(widget));
    if (g_strcmp0(label, paint->label) == 0) return;
    g_free(paint->label);
    paint->label = g_strdup(label);
    paint->label_revision++;
    paint->painted_label_revision = 0;
    /* A new label can still expose its previous allocation. Invalidate now,
     * then request a complete later frame, never certify the in-progress one.
     * Unchanged render notifications must not keep restarting this boundary. */
    if (paint->clock && gtk_widget_get_mapped(widget) && !paint->request_source)
        paint->request_source = g_idle_add_full(G_PRIORITY_DEFAULT_IDLE,
            control_request_frame, g_object_ref(widget), g_object_unref);
}
typedef struct {
    GtkRequisition minimum, natural;
    gint64 at;
} PopupMeasure;
static void remember_popup_measure(GtkWidget *widget) {
    if (!g_getenv("TYPELAY_FIXTURE_GLOSSARY_COMMAND")) return;
    GtkNative *native = gtk_widget_get_native(widget);
    if (!native || !GTK_IS_POPOVER(native)
        || g_strcmp0(gtk_widget_get_name(GTK_WIDGET(native)), "typelay-overlay") != 0) return;
    PopupMeasure *size = g_new0(PopupMeasure, 1);
    gtk_widget_get_preferred_size(GTK_WIDGET(native), &size->minimum, &size->natural);
    size->at = g_get_monotonic_time() / 1000;
    g_object_set_data_full(G_OBJECT(native), "fixture-popup-measure", size, g_free);
}
static void control_paint_map(GtkWidget *widget, gpointer unused) {
    (void)unused;
    ControlPaint *paint = g_object_get_data(G_OBJECT(widget), "fixture-control-paint");
    control_paint_clear(paint);
    if (GTK_IS_BUTTON(widget)) {
        paint->label = g_strdup(gtk_button_get_label(GTK_BUTTON(widget)));
        paint->label_revision++;
    }
    paint->map_generation++;
    paint->mapped_at = g_get_monotonic_time() / 1000;
    remember_popup_measure(widget);
    GdkFrameClock *clock = gtk_widget_get_frame_clock(widget);
    GtkNative *native = gtk_widget_get_native(widget);
    GdkSurface *surface = native ? gtk_native_get_surface(native) : NULL;
    paint->surface_instance = surface ? language_instance(G_OBJECT(surface)) : 0;
    control_map_observation(widget, paint, TRUE);
    if (!clock || !surface) return;
    paint->clock = g_object_ref(clock);
    paint->requested_frame = gdk_frame_clock_get_frame_counter(clock);
    paint->layout_handler = g_signal_connect_object(clock, "layout",
        G_CALLBACK(control_layout), widget, G_CONNECT_AFTER);
    paint->paint_handler = g_signal_connect_object(clock, "paint",
        G_CALLBACK(control_paint), widget, G_CONNECT_AFTER);
    paint->handler = g_signal_connect_object(clock, "after-paint",
        G_CALLBACK(control_after_paint), widget, G_CONNECT_AFTER);
    /* Map can run during layout; request once from the next idle dispatch so
     * its phases cannot be consumed by that in-progress frame. Also covers
     * discovery after map when no further natural frame would be scheduled. */
    paint->request_source = g_idle_add_full(G_PRIORITY_DEFAULT_IDLE,
        control_request_frame, g_object_ref(widget), g_object_unref);
}
static void control_paint_unmap(GtkWidget *widget, gpointer unused) {
    (void)unused;
    ControlPaint *paint = g_object_get_data(G_OBJECT(widget), "fixture-control-paint");
    control_map_observation(widget, paint, FALSE);
    control_paint_clear(paint);
}
static void report_control_paint(JsonBuilder *builder, GtkWidget *widget, JsonNode *geometry) {
    ControlPaint *paint = g_object_get_data(G_OBJECT(widget), "fixture-control-paint");
    if (!paint) {
        paint = g_new0(ControlPaint, 1);
        g_object_set_data_full(G_OBJECT(widget), "fixture-control-paint", paint, control_paint_free);
        g_signal_connect(widget, "map", G_CALLBACK(control_paint_map), NULL);
        g_signal_connect(widget, "unmap", G_CALLBACK(control_paint_unmap), NULL);
        if (GTK_IS_BUTTON(widget))
            g_signal_connect(widget, "notify::label", G_CALLBACK(control_label_changed), NULL);
        if (gtk_widget_get_mapped(widget)) control_paint_map(widget, NULL);
    }
    GdkFrameClock *clock = gtk_widget_get_frame_clock(widget);
    guint64 surface_instance = geometry
        ? (guint64)json_object_get_int_member(json_node_get_object(geometry), "surfaceInstance") : 0;
    if (gtk_widget_get_mapped(widget) && (paint->clock != clock
        || (surface_instance && paint->surface_instance != surface_instance)))
        control_paint_map(widget, NULL);
    gint64 frame = paint->clock ? gdk_frame_clock_get_frame_counter(paint->clock) : -1;
    gboolean ready = gtk_widget_get_mapped(widget) && geometry && paint->geometry
        && paint->painted_at >= paint->mapped_at && paint->painted_at > 0
        && paint->painted_map_generation == paint->map_generation
        && paint->painted_label_revision == paint->label_revision
        && paint->painted_frame <= frame && json_node_equal(geometry, paint->geometry);
    json_builder_set_member_name(builder, "paintObservation");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "ready"); json_builder_add_boolean_value(builder, ready);
    json_builder_set_member_name(builder, "labelRevision"); json_builder_add_int_value(builder, paint->label_revision);
    json_builder_set_member_name(builder, "paintedLabelRevision"); json_builder_add_int_value(builder, paint->painted_label_revision);
    json_builder_set_member_name(builder, "clockInstance"); json_builder_add_int_value(builder, language_instance(G_OBJECT(paint->clock)));
    json_builder_set_member_name(builder, "mapGeneration"); json_builder_add_int_value(builder, paint->map_generation);
    json_builder_set_member_name(builder, "paintedMapGeneration"); json_builder_add_int_value(builder, paint->painted_map_generation);
    json_builder_set_member_name(builder, "requestedFrame"); json_builder_add_int_value(builder, paint->requested_frame);
    json_builder_set_member_name(builder, "mappedAtMonoMs"); json_builder_add_int_value(builder, paint->mapped_at);
    json_builder_set_member_name(builder, "frame"); json_builder_add_int_value(builder, frame);
    json_builder_set_member_name(builder, "layoutFrame"); json_builder_add_int_value(builder, paint->completed_layout_frame);
    json_builder_set_member_name(builder, "paintFrame"); json_builder_add_int_value(builder, paint->completed_paint_frame);
    json_builder_set_member_name(builder, "paintedFrame"); json_builder_add_int_value(builder, paint->painted_frame);
    json_builder_set_member_name(builder, "paintedAtMonoMs"); json_builder_add_int_value(builder, paint->painted_at);
    json_builder_set_member_name(builder, "geometry");
    if (paint->geometry) json_builder_add_value(builder, json_node_copy(paint->geometry));
    else json_builder_add_null_value(builder);
    if (paint->last_map || paint->last_unmap) {
        json_builder_set_member_name(builder, "mapLifecycle");
        json_builder_begin_object(builder);
        const char *names[] = {"lastMapObserved", "lastUnmap"};
        JsonObject *records[] = {paint->last_map, paint->last_unmap};
        for (guint i = 0; i < G_N_ELEMENTS(records); i++) {
            json_builder_set_member_name(builder, names[i]);
            if (records[i]) {
                JsonNode *node = json_node_new(JSON_NODE_OBJECT);
                json_node_set_object(node, records[i]);
                json_builder_add_value(builder, node);
            } else json_builder_add_null_value(builder);
        }
        json_builder_end_object(builder);
    }
    json_builder_end_object(builder);
}

static void report_control_surface(JsonBuilder *builder, GtkWidget *widget) {
    GtkEventControllerMotion *motion = g_object_get_data(G_OBJECT(widget), "fixture-control-motion");
    if (!motion) {
        GtkEventController *observer = gtk_event_controller_motion_new();
        motion = GTK_EVENT_CONTROLLER_MOTION(observer);
        g_signal_connect(observer, "enter", G_CALLBACK(control_pointer_enter), NULL);
        g_signal_connect(observer, "leave", G_CALLBACK(control_pointer_leave), NULL);
        g_signal_connect(observer, "motion", G_CALLBACK(control_pointer_motion), NULL);
        g_signal_connect(widget, "unmap", G_CALLBACK(control_pointer_unmap), NULL);
        g_object_set_data(G_OBJECT(widget), "fixture-control-motion", observer);
        g_object_set_data(G_OBJECT(widget), "fixture-control-motion-attached-at",
            (gpointer)(gintptr)(g_get_monotonic_time() / 1000));
        gtk_widget_add_controller(widget, observer);
    }
    GtkEventControllerMotion *capture_motion = g_object_get_data(G_OBJECT(widget), "fixture-control-capture-motion");
    if (!capture_motion) {
        GtkEventController *observer = gtk_event_controller_motion_new();
        capture_motion = GTK_EVENT_CONTROLLER_MOTION(observer);
        gtk_event_controller_set_propagation_phase(observer, GTK_PHASE_CAPTURE);
        g_signal_connect(observer, "enter", G_CALLBACK(control_capture_pointer_enter), NULL);
        g_signal_connect(observer, "leave", G_CALLBACK(control_capture_pointer_leave), NULL);
        g_signal_connect(observer, "motion", G_CALLBACK(control_pointer_motion), NULL);
        g_signal_connect(widget, "unmap", G_CALLBACK(control_capture_pointer_unmap), NULL);
        g_object_set_data(G_OBJECT(widget), "fixture-control-capture-motion", observer);
        g_object_set_data(G_OBJECT(widget), "fixture-control-capture-motion-attached-at",
            (gpointer)(gintptr)(g_get_monotonic_time() / 1000));
        gtk_widget_add_controller(widget, observer);
    }
    json_builder_set_member_name(builder, "pointerInside");
    json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget)
        && gtk_event_controller_motion_contains_pointer(motion));
    json_builder_set_member_name(builder, "pointerDispatchObservation");
    report_control_pointer_event(builder, motion);
    json_builder_set_member_name(builder, "pointerEnteredAtMonoMs");
    json_builder_add_int_value(builder,
        (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-control-enter-at"));
    json_builder_set_member_name(builder, "pointerLeftAtMonoMs");
    json_builder_add_int_value(builder,
        (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-control-leave-at"));
    json_builder_set_member_name(builder, "pointerObserverAttachedAtMonoMs");
    json_builder_add_int_value(builder,
        (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-control-motion-attached-at"));
    json_builder_set_member_name(builder, "pointerCaptureObservation");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "inside");
    json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget)
        && gtk_event_controller_motion_contains_pointer(capture_motion));
    json_builder_set_member_name(builder, "dispatch");
    report_control_pointer_event(builder, capture_motion);
    json_builder_set_member_name(builder, "enteredAtMonoMs");
    json_builder_add_int_value(builder,
        (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-control-capture-enter-at"));
    json_builder_set_member_name(builder, "leftAtMonoMs");
    json_builder_add_int_value(builder,
        (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-control-capture-leave-at"));
    json_builder_set_member_name(builder, "attachedAtMonoMs");
    json_builder_add_int_value(builder,
        (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-control-capture-motion-attached-at"));
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "canTarget");
    json_builder_add_boolean_value(builder, gtk_widget_get_can_target(widget));
    json_builder_set_member_name(builder, "canFocus");
    json_builder_add_boolean_value(builder, gtk_widget_get_can_focus(widget));
    GtkNative *native = gtk_widget_get_native(widget);
    GdkSurface *surface = native ? gtk_native_get_surface(native) : NULL;
    GdkSurface *parent = gtk_native_get_surface(GTK_NATIVE(report_window));
    GdkSeat *seat = gdk_display_get_default_seat(gtk_widget_get_display(report_window));
    GdkDevice *pointer = seat ? gdk_seat_get_pointer(seat) : NULL;
    double px = 0, py = 0;
    GdkSurface *pointer_surface = pointer ? gdk_device_get_surface_at_position(pointer, &px, &py) : NULL;
    json_builder_set_member_name(builder, "pointerObservation");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "status");
    json_builder_add_string_value(builder, pointer_surface ? "captured" : "unavailable");
    if (pointer_surface) {
        json_builder_set_member_name(builder, "surface");
        json_builder_add_string_value(builder, pointer_surface == surface ? "control-native"
            : pointer_surface == parent ? "fixture-window" : "other");
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, px);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, py);
        GtkNative *pointer_native = gtk_native_get_for_surface(pointer_surface);
        /* Report a pick only inside this owned fixture, never other app names. */
        if (pointer_native && gtk_widget_get_root(GTK_WIDGET(pointer_native)) == GTK_ROOT(report_window)) {
            json_builder_set_member_name(builder, "surfaceInstance");
            json_builder_add_int_value(builder, language_instance(G_OBJECT(pointer_surface)));
            json_builder_set_member_name(builder, "nativeInstance");
            json_builder_add_int_value(builder, language_instance(G_OBJECT(pointer_native)));
            double ox, oy;
            gtk_native_get_surface_transform(pointer_native, &ox, &oy);
            GtkWidget *picked = gtk_widget_pick(GTK_WIDGET(pointer_native), px + ox, py + oy, GTK_PICK_DEFAULT);
            json_builder_set_member_name(builder, "pick");
            if (picked) json_builder_add_string_value(builder, gtk_widget_get_name(picked));
            else json_builder_add_null_value(builder);
            /* A bounded owned-widget path distinguishes a real overlapping
             * sibling from a descendant whose motion observer missed entry.
             * Never inspect label text or another application's widget tree. */
            json_builder_set_member_name(builder, "pickedAncestors");
            json_builder_begin_array(builder);
            for (guint depth = 0; picked && depth < 8; depth++, picked = gtk_widget_get_parent(picked)) {
                json_builder_begin_object(builder);
                json_builder_set_member_name(builder, "type");
                json_builder_add_string_value(builder, G_OBJECT_TYPE_NAME(picked));
                json_builder_set_member_name(builder, "instance");
                json_builder_add_int_value(builder, language_instance(G_OBJECT(picked)));
                json_builder_set_member_name(builder, "isObservedControl");
                json_builder_add_boolean_value(builder, picked == widget);
                json_builder_set_member_name(builder, "canTarget");
                json_builder_add_boolean_value(builder, gtk_widget_get_can_target(picked));
                json_builder_set_member_name(builder, "canFocus");
                json_builder_add_boolean_value(builder, gtk_widget_get_can_focus(picked));
                json_builder_set_member_name(builder, "visible");
                json_builder_add_boolean_value(builder, gtk_widget_get_visible(picked));
                json_builder_set_member_name(builder, "mapped");
                json_builder_add_boolean_value(builder, gtk_widget_get_mapped(picked));
                json_builder_end_object(builder);
                if (picked == report_window) break;
            }
            json_builder_end_array(builder);
        }
    }
    json_builder_end_object(builder);
    JsonNode *geometry = control_surface_geometry(widget);
    json_builder_set_member_name(builder, "surfaceGeometry");
    if (geometry) json_builder_add_value(builder, json_node_copy(geometry));
    else json_builder_add_null_value(builder);
    if (GTK_IS_SCROLLED_WINDOW(widget)
        && g_strcmp0(gtk_widget_get_name(widget), "typelay-language-menu") == 0) {
        GtkWidget *viewport = gtk_scrolled_window_get_child(GTK_SCROLLED_WINDOW(widget));
        JsonNode *clip = viewport ? control_surface_geometry(viewport) : NULL;
        json_builder_set_member_name(builder, "scrollViewport");
        if (clip) json_builder_add_value(builder, clip);
        else json_builder_add_null_value(builder);
        GtkAdjustment *adjustment = gtk_scrolled_window_get_vadjustment(GTK_SCROLLED_WINDOW(widget));
        json_builder_set_member_name(builder, "scrollValue");
        json_builder_add_double_value(builder, gtk_adjustment_get_value(adjustment));
    }
    if (GTK_IS_POPOVER(widget) && (g_strcmp0(gtk_widget_get_name(widget), "typelay-more-popup") == 0
        || g_strcmp0(gtk_widget_get_name(widget), "typelay-language-configuration") == 0)) {
        json_builder_set_member_name(builder, "autohide");
        json_builder_add_boolean_value(builder, gtk_popover_get_autohide(GTK_POPOVER(widget)));
        json_builder_set_member_name(builder, "hasArrow");
        json_builder_add_boolean_value(builder, gtk_popover_get_has_arrow(GTK_POPOVER(widget)));
    }
    report_control_paint(builder, widget, geometry);
    g_clear_pointer(&geometry, json_node_unref);
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
    GtkWidget *parent = gtk_widget_get_parent(widget);
    if (GTK_IS_LABEL(widget) && gtk_label_get_wrap(GTK_LABEL(widget)) && parent
        && g_strcmp0(gtk_widget_get_name(parent), "typelay-glossary-form") == 0)
        name = "fixture-glossary-feedback";
    if (GTK_IS_LABEL(widget) && gtk_widget_has_css_class(widget, "language-feedback"))
        name = "fixture-language-feedback";
    if (expand || language_control(name) || g_str_equal(name, "fixture-language-feedback")
        || g_str_equal(name, "fixture-glossary-feedback")
        || g_str_equal(name, "typelay-dot") || g_str_equal(name, "typelay-skeleton")
        || (g_getenv("TYPELAY_FIXTURE_GLOSSARY_COMMAND") && g_str_equal(name, "typelay-status"))) {
        json_builder_set_member_name(builder, name);
        json_builder_begin_object(builder);
        const char *stage = g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE");
        gboolean language_trigger = g_str_equal(name, "typelay-menu-trigger")
            && stage && (g_str_equal(stage, "language-options") || g_str_has_prefix(stage, "language-retention-")
                || g_str_has_prefix(stage, "language-configuration-"));
        gboolean language_menu = stage && (g_str_equal(stage, "language-options")
            || g_str_has_prefix(stage, "language-retention-")
            || g_str_has_prefix(stage, "language-configuration-")) && language_control(name);
        if ((g_getenv("TYPELAY_FIXTURE_GLOSSARY_COMMAND") && language_control(name)) || language_trigger || language_menu)
            report_control_surface(builder, widget);
        if (expand) {
            GtkEventControllerMotion *motion = g_object_get_data(G_OBJECT(widget), "fixture-expand-motion");
            json_builder_set_member_name(builder, "pointerInside");
            json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget) && motion
                && gtk_event_controller_motion_contains_pointer(motion));
            json_builder_set_member_name(builder, "pointerEnteredAtMs");
            json_builder_add_int_value(builder,
                (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-expand-enter-at"));
            json_builder_set_member_name(builder, "scale");
            json_builder_add_int_value(builder, gtk_widget_get_scale_factor(widget));
        }
        json_builder_set_member_name(builder, "instance");
        json_builder_add_int_value(builder, language_instance(G_OBJECT(widget)));
        json_builder_set_member_name(builder, "visible");
        json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget));
        if (g_str_equal(name, "typelay-dot")) {
            GdkRGBA color;
#if GTK_CHECK_VERSION(4, 10, 0)
            gtk_widget_get_color(widget, &color);
#else
            gtk_style_context_get_color(gtk_widget_get_style_context(widget), &color);
#endif
            char *formatted = gdk_rgba_to_string(&color);
            json_builder_set_member_name(builder, "foreground"); json_builder_add_string_value(builder, formatted);
            g_free(formatted);
        }
        json_builder_set_member_name(builder, "enabled");
        json_builder_add_boolean_value(builder, gtk_widget_is_sensitive(widget));
        GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(report_window));
        json_builder_set_member_name(builder, "focused");
        json_builder_add_boolean_value(builder, focus && (focus == widget || gtk_widget_is_ancestor(focus, widget)));
        json_builder_set_member_name(builder, "focusVisible");
        json_builder_add_boolean_value(builder, (gtk_widget_get_state_flags(widget) & GTK_STATE_FLAG_FOCUS_VISIBLE) != 0);
        if (GTK_IS_LABEL(widget)) {
            json_builder_set_member_name(builder, "text");
            json_builder_add_string_value(builder, gtk_label_get_text(GTK_LABEL(widget)));
            if (g_str_equal(name, "typelay-status")) {
                const char *tooltip = gtk_widget_get_tooltip_text(widget);
                json_builder_set_member_name(builder, "tooltip");
                json_builder_add_string_value(builder, tooltip ? tooltip : "");
            }
        }
        if (GTK_IS_EDITABLE(widget)) {
            json_builder_set_member_name(builder, "text");
            json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(widget)));
        }
        if (GTK_IS_BUTTON(widget)) {
            const char *description = gtk_widget_get_tooltip_text(widget);
            json_builder_set_member_name(builder, "description"); json_builder_add_string_value(builder, description ? description : "");
            const char *label = gtk_button_get_label(GTK_BUTTON(widget));
            json_builder_set_member_name(builder, "text");
            json_builder_add_string_value(builder, label ? label : "");
        }
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        if (gtk_widget_compute_point(widget, report_window, &origin, &point)) {
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width"); json_builder_add_int_value(builder, gtk_widget_get_width(widget));
            json_builder_set_member_name(builder, "height"); json_builder_add_int_value(builder, gtk_widget_get_height(widget));
        }
        if (GTK_IS_MENU_BUTTON(widget)) {
            GtkPopover *popup = gtk_menu_button_get_popover(GTK_MENU_BUTTON(widget));
            GtkWidget *scroll = popup ? gtk_popover_get_child(popup) : NULL;
            GtkWidget *list = GTK_IS_SCROLLED_WINDOW(scroll) ? gtk_scrolled_window_get_child(GTK_SCROLLED_WINDOW(scroll)) : NULL;
            if (GTK_IS_LIST_VIEW(list)) {
                watch_language_popup(widget, popup);
                json_builder_set_member_name(builder, "popupActive");
                json_builder_add_boolean_value(builder, gtk_menu_button_get_active(GTK_MENU_BUTTON(widget)));
                json_builder_set_member_name(builder, "popupShown");
                json_builder_add_boolean_value(builder, gtk_widget_get_visible(GTK_WIDGET(popup)));
                GtkSingleSelection *selection = GTK_SINGLE_SELECTION(gtk_list_view_get_model(GTK_LIST_VIEW(list)));
                GListModel *model = gtk_single_selection_get_model(selection);
                json_builder_set_member_name(builder, "modelInstance");
                json_builder_add_int_value(builder, language_instance(G_OBJECT(model)));
                json_builder_set_member_name(builder, "selected");
                json_builder_add_int_value(builder, gtk_single_selection_get_selected(selection));
                json_builder_set_member_name(builder, "popupVisible");
                json_builder_add_boolean_value(builder, gtk_widget_get_mapped(GTK_WIDGET(popup)));
                json_builder_set_member_name(builder, "buttonFocused");
                json_builder_add_boolean_value(builder, focus && gtk_widget_get_mapped(focus)
                    && (focus == widget || gtk_widget_is_ancestor(focus, widget))
                    && focus != GTK_WIDGET(popup) && !gtk_widget_is_ancestor(focus, GTK_WIDGET(popup)));
                json_builder_set_member_name(builder, "popupFocused");
                json_builder_add_boolean_value(builder, focus && gtk_widget_get_mapped(GTK_WIDGET(popup))
                    && (focus == list || gtk_widget_is_ancestor(focus, list)));
                json_builder_set_member_name(builder, "items");
                json_builder_begin_array(builder);
                if (model) for (guint index = 0; index < g_list_model_get_n_items(model); index++) {
                    GObject *item = g_list_model_get_item(model, index);
                    if (GTK_IS_STRING_OBJECT(item))
                        json_builder_add_string_value(builder, gtk_string_object_get_string(GTK_STRING_OBJECT(item)));
                    g_object_unref(item);
                }
                json_builder_end_array(builder);
            }
        }
        json_builder_end_object(builder);
    }
    for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child))
        report_language_control(child, data);
}

#include "gtk_motion.inc"

/* Public widget discovery, also used by the ordinary UI observations above. */
static GtkWidget *find_named_widget(GtkWidget *widget, const char *name) {
    if (g_strcmp0(gtk_widget_get_name(widget), name) == 0) return widget;
    for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child)) {
        GtkWidget *found = find_named_widget(child, name);
        if (found) return found;
    }
    return NULL;
}
/* Bounded public size facts survive a GtkPopover popdown after allocation. */
static void report_overlay_popup(JsonBuilder *builder) {
    GtkWidget *popup = find_named_widget(report_window, "typelay-overlay");
    json_builder_set_member_name(builder, "overlayPopup");
    if (!popup || !GTK_IS_POPOVER(popup)) { json_builder_add_null_value(builder); return; }
    GtkRequisition minimum, natural;
    gtk_widget_get_preferred_size(popup, &minimum, &natural);
    PopupMeasure *last = g_object_get_data(G_OBJECT(popup), "fixture-popup-measure");
    gboolean previous = minimum.height == 0 && last;
    if (previous) { minimum = last->minimum; natural = last->natural; }
    GdkSurface *surface = gtk_native_get_surface(GTK_NATIVE(popup));
    GdkRectangle anchor = {0};
    gboolean has_anchor = gtk_popover_get_pointing_to(GTK_POPOVER(popup), &anchor);
    const char *names[] = {"minimumWidth", "minimumHeight", "naturalWidth", "naturalHeight",
        "surfaceWidth", "surfaceHeight", "anchorX", "anchorY", "anchorWidth", "anchorHeight"};
    gint values[] = {minimum.width, minimum.height, natural.width, natural.height,
        surface ? gdk_surface_get_width(surface) : 0, surface ? gdk_surface_get_height(surface) : 0,
        anchor.x, anchor.y, anchor.width, anchor.height};
    json_builder_begin_object(builder);
    for (guint index = 0; index < G_N_ELEMENTS(names); index++) {
        json_builder_set_member_name(builder, names[index]);
        json_builder_add_int_value(builder, values[index]);
    }
    json_builder_set_member_name(builder, "visible");
    json_builder_add_boolean_value(builder, gtk_widget_get_visible(popup));
    json_builder_set_member_name(builder, "mapped");
    json_builder_add_boolean_value(builder, gtk_widget_get_mapped(popup));
    json_builder_set_member_name(builder, "surfaceMapped");
    json_builder_add_boolean_value(builder, surface && gdk_surface_get_mapped(surface));
    json_builder_set_member_name(builder, "measurement");
    json_builder_add_string_value(builder, previous ? "last-mapped" : "current");
    json_builder_set_member_name(builder, "measuredAtMs");
    json_builder_add_int_value(builder, previous ? last->at : g_get_monotonic_time() / 1000);
    json_builder_set_member_name(builder, "hasAnchor");
    json_builder_add_boolean_value(builder, has_anchor);
    json_builder_end_object(builder);
}
#include "gtk_glossary.inc"
static gboolean report_state(gpointer unused) {
    (void)unused;
    gint64 started_at = g_get_monotonic_time(), ime_observed_at = 0;
    glossary_host_command();
    remove_session_controller(report_window, FALSE);
    GtkWidget *active = other_window && gtk_window_is_active(GTK_WINDOW(other_window)) ? other_window : report_window;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(active));
    GtkWidget *focus_field = focus;
    while (focus_field && !GTK_IS_ENTRY(focus_field) && !GTK_IS_TEXT_VIEW(focus_field) && !GTK_IS_PASSWORD_ENTRY(focus_field))
        focus_field = gtk_widget_get_parent(focus_field);
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    if (g_getenv("TYPELAY_TRACE") && previous_report_timing[0]) {
        /* The previous completed callback distinguishes observation/layout
         * time from publication without adding another file write or probe. */
        const char *names[] = {"startedAtUs", "imeObservedAtUs", "serializedAtUs", "publishedAtUs"};
        json_builder_set_member_name(builder, "previousReportTiming");
        json_builder_begin_object(builder);
        for (guint i = 0; i < G_N_ELEMENTS(names); i++) {
            json_builder_set_member_name(builder, names[i]);
            json_builder_add_int_value(builder, previous_report_timing[i]);
        }
        json_builder_end_object(builder);
    }
    motion_report(builder);
    glossary_report(builder);
    json_builder_set_member_name(builder, "observedAtMs");
    json_builder_add_int_value(builder, g_get_monotonic_time() / 1000);
    json_builder_set_member_name(builder, "first");
    json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(report_first)));
    json_builder_set_member_name(builder, "second");
    json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(report_second)));
    if (other_entry) {
        json_builder_set_member_name(builder, "other");
        json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(other_entry)));
    }
    json_builder_set_member_name(builder, "focus");
    json_builder_add_string_value(builder, focus_field ? gtk_widget_get_name(focus_field) : "");
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
        json_builder_set_member_name(builder, "focusedImeClear");
        json_builder_add_boolean_value(builder, valid && ime_clear(&state));
        json_builder_set_member_name(builder, "focusedImeComposing");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_COMPOSING));
        ime_observed_at = g_get_monotonic_time();
        json_builder_set_member_name(builder, "fullscreen");
        json_builder_add_boolean_value(builder,
            gtk_window_is_fullscreen(GTK_WINDOW(report_window)));
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_width(report_window));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_height(report_window));
        json_builder_set_member_name(builder, "scale");
        json_builder_add_int_value(builder, gtk_widget_get_scale_factor(report_window));
        if (g_getenv("TYPELAY_FIXTURE_GLOSSARY_COMMAND")) report_overlay_popup(builder);
        json_builder_set_member_name(builder, "overlayControls");
        json_builder_begin_object(builder);
        report_language_control(report_window, builder);
        json_builder_end_object(builder);
        GtkWidget *candidate = NULL;
        find_candidate(report_window, &candidate);
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        json_builder_set_member_name(builder, "candidate");
        if (candidate && gtk_widget_get_mapped(candidate)
            && gtk_widget_compute_point(candidate, report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width");
            json_builder_add_int_value(builder, gtk_widget_get_width(candidate));
            json_builder_set_member_name(builder, "height");
            json_builder_add_int_value(builder, gtk_widget_get_height(candidate));
            report_candidate_natural_frames(builder, candidate);
            json_builder_set_member_name(builder, "editable");
            json_builder_add_boolean_value(builder, gtk_text_view_get_editable(GTK_TEXT_VIEW(candidate)));
            json_builder_set_member_name(builder, "focused");
            json_builder_add_boolean_value(builder, gtk_widget_has_focus(candidate));
            json_builder_set_member_name(builder, "editablePreview");
            json_builder_add_boolean_value(builder, gtk_widget_has_css_class(candidate, "editable-preview"));
            GdkRGBA foreground;
#if GTK_CHECK_VERSION(4, 10, 0)
            gtk_widget_get_color(candidate, &foreground);
#else
            gtk_style_context_get_color(gtk_widget_get_style_context(candidate), &foreground);
#endif
            char *color = gdk_rgba_to_string(&foreground);
            json_builder_set_member_name(builder, "foreground"); json_builder_add_string_value(builder, color);
            g_free(color);
            PangoContext *font_context = gtk_widget_get_pango_context(candidate);
            const PangoFontDescription *font = pango_context_get_font_description(font_context);
            double pixels = (double)pango_font_description_get_size(font) / PANGO_SCALE;
            if (!pango_font_description_get_size_is_absolute(font)) {
                double dpi = pango_cairo_context_get_resolution(font_context);
                if (dpi <= 0) dpi = pango_cairo_font_map_get_resolution(PANGO_CAIRO_FONT_MAP(pango_context_get_font_map(font_context)));
                pixels *= dpi / 72.0;
            }
            json_builder_set_member_name(builder, "fontPixels");
            json_builder_add_double_value(builder, pixels);
            json_builder_set_member_name(builder, "fontFamily");
            json_builder_add_string_value(builder, pango_font_description_get_family(font));
            /* Public content/draw bounds and requested viewport geometry, not
             * a forced measure/allocation. Keep the failing 2px observation. */
            json_builder_set_member_name(builder, "layoutGeometry");
            json_builder_begin_object(builder);
            graphene_rect_t border_bounds;
            if (gtk_widget_compute_bounds(candidate, candidate, &border_bounds)) {
                json_builder_set_member_name(builder, "boundsX");
                json_builder_add_double_value(builder, border_bounds.origin.x);
                json_builder_set_member_name(builder, "boundsY");
                json_builder_add_double_value(builder, border_bounds.origin.y);
                json_builder_set_member_name(builder, "boundsWidth");
                json_builder_add_double_value(builder, border_bounds.size.width);
                json_builder_set_member_name(builder, "boundsHeight");
                json_builder_add_double_value(builder, border_bounds.size.height);
            }
            GtkWidget *scroll = gtk_widget_get_ancestor(candidate, GTK_TYPE_SCROLLED_WINDOW);
            if (scroll) {
                gint requested_width, requested_height;
                gtk_widget_get_size_request(scroll, &requested_width, &requested_height);
                json_builder_set_member_name(builder, "scrollWidth");
                json_builder_add_int_value(builder, gtk_widget_get_width(scroll));
                json_builder_set_member_name(builder, "scrollHeight");
                json_builder_add_int_value(builder, gtk_widget_get_height(scroll));
                json_builder_set_member_name(builder, "scrollRequestedWidth");
                json_builder_add_int_value(builder, requested_width);
                json_builder_set_member_name(builder, "scrollRequestedHeight");
                json_builder_add_int_value(builder, requested_height);
            }
            json_builder_set_member_name(builder, "fontWeight");
            json_builder_add_int_value(builder, pango_font_description_get_weight(font));
            json_builder_end_object(builder);
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(candidate));
            GtkTextIter first, last;
            gtk_text_buffer_get_bounds(buffer, &first, &last);
            char *text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
            json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, text);
            report_font_runs(builder, candidate, text, "fontRuns");
            gboolean host_map = pango_context_get_font_map(font_context)
                == pango_context_get_font_map(gtk_widget_get_pango_context(report_window));
            json_builder_set_member_name(builder, "usesHostFontMap");
            json_builder_add_boolean_value(builder, host_map);
            if (host_map) report_font_runs(builder, report_window, text, "hostFontRuns");
            g_free(text);
            json_builder_end_object(builder);
        } else json_builder_add_null_value(builder);
    }
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
    json_builder_set_member_name(builder, "canUndo"); json_builder_add_boolean_value(builder, gtk_text_buffer_get_can_undo(multiline));
    graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
    if (gtk_widget_compute_point(report_multiline, report_window, &origin, &point)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
    }
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "excludedTextViews");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        if (gtk_widget_compute_point(excluded_views[i], report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name"); json_builder_add_string_value(builder, gtk_widget_get_name(excluded_views[i]));
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_end_object(builder);
        }
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "protectedFields");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(protected_fields); i++) {
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        GtkWidget *field = protected_fields[i];
        if (field && gtk_widget_compute_point(field, report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name");
            json_builder_add_string_value(builder, gtk_widget_get_name(field));
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width"); json_builder_add_int_value(builder, gtk_widget_get_width(field));
            json_builder_set_member_name(builder, "height"); json_builder_add_int_value(builder, gtk_widget_get_height(field));
            json_builder_end_object(builder);
        }
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "tabKeys");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "down"); json_builder_add_int_value(builder, tab_down);
    json_builder_set_member_name(builder, "up"); json_builder_add_int_value(builder, tab_up);
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "languageTransitions");
    json_builder_begin_array(builder);
    for (guint index = 0; index < language_transition_count; index++) {
        JsonNode *transition = json_node_new(JSON_NODE_OBJECT);
        json_node_set_object(transition, language_transitions[index]);
        json_builder_add_value(builder, transition);
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "focusSequence"); json_builder_add_int_value(builder, focus_sequence);
    json_builder_set_member_name(builder, "focusTransitions");
    json_builder_begin_array(builder);
    guint first = focus_sequence > G_N_ELEMENTS(focus_transitions) ? focus_sequence - G_N_ELEMENTS(focus_transitions) : 0;
    for (guint index = first; index < focus_sequence; index++) {
        JsonNode *transition = json_node_new(JSON_NODE_OBJECT);
        json_node_set_object(transition, focus_transitions[index % G_N_ELEMENTS(focus_transitions)]);
        json_builder_add_value(builder, transition);
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
    gint64 serialized_at = g_get_monotonic_time();
    if (g_strcmp0(data, last_report) != 0) {
        int failure = fixture_publish_state(report_path, data);
        if (failure) g_error("state write: %s", strerror(failure));
        g_free(last_report);
        last_report = g_strdup(data);
    }
    previous_report_timing[0] = started_at;
    previous_report_timing[1] = ime_observed_at;
    previous_report_timing[2] = serialized_at;
    previous_report_timing[3] = g_get_monotonic_time();
    g_free(data);
    g_object_unref(generator);
    json_node_free(node);
    g_object_unref(builder);
    return G_SOURCE_CONTINUE;
}

/* An actual GTK client, not a production backend or a source-string assertion. */
static gboolean close_fixture(GtkWindow *window, gpointer data) {
    (void)window; g_main_loop_quit(data); return FALSE;
}
int main(void) {
    gtk_init();
    GMainLoop *loop = g_main_loop_new(NULL, FALSE);
    GtkWidget *window = gtk_window_new();
    gtk_window_set_title(GTK_WINDOW(window), "TypeLay GTK4 contract fixture");
    gtk_window_set_default_size(GTK_WINDOW(window), 650, 480);
    GtkWidget *title = gtk_header_bar_new();
    gtk_header_bar_set_title_widget(GTK_HEADER_BAR(title), gtk_label_new("TypeLay input contracts"));
    gtk_header_bar_set_show_title_buttons(GTK_HEADER_BAR(title), TRUE);
    gtk_window_set_titlebar(GTK_WINDOW(window), title);
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
    gtk_widget_set_margin_start(box, 32); gtk_widget_set_margin_end(box, 32);
    gtk_widget_set_margin_top(box, 32); gtk_widget_set_margin_bottom(box, 32);
    gtk_window_set_child(GTK_WINDOW(window), box);
    GtkWidget *first = gtk_entry_new(), *second = gtk_entry_new(), *password = gtk_entry_new();
    gtk_widget_set_name(first, "fixture-first");
    gtk_widget_set_name(second, "fixture-second");
    gtk_widget_set_name(password, "fixture-password");
    const char *initial = g_getenv("TYPELAY_FIXTURE_INITIAL_TEXT");
    gtk_editable_set_text(GTK_EDITABLE(first), initial ? initial : "alpha 😀 omega");
    gtk_editable_set_text(GTK_EDITABLE(second), "second field");
    gtk_entry_set_visibility(GTK_ENTRY(password), FALSE);
    gtk_editable_set_text(GTK_EDITABLE(password), "fixture-only-private");
    gtk_box_append(GTK_BOX(box), gtk_label_new("Ordinary entries and excluded password field"));
    gtk_box_append(GTK_BOX(box), first);
    gtk_box_append(GTK_BOX(box), second);
    /* Native automationId is the buildable ID, not the CSS/widget name. */
    GtkBuilder *identities = gtk_builder_new();
    gtk_builder_expose_object(identities, "fixture-second", G_OBJECT(second));
    g_object_unref(identities);
    g_assert_cmpstr(gtk_buildable_get_buildable_id(GTK_BUILDABLE(second)), ==, "fixture-second");
    gtk_box_append(GTK_BOX(box), password);
    GtkWidget *password_entry = gtk_password_entry_new();
    gtk_widget_set_name(password_entry, "fixture-password-entry");
    gtk_editable_set_text(GTK_EDITABLE(password_entry), "synthetic-secret");
    gtk_box_append(GTK_BOX(box), password_entry);
    protected_fields[0] = password;
    protected_fields[1] = password_entry;
    GtkWidget *view = gtk_text_view_new();
    gtk_widget_set_name(view, "fixture-multiline");
    GtkTextBuffer *text_buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(view));
    gtk_text_buffer_set_enable_undo(text_buffer, TRUE);
    gtk_text_buffer_set_text(text_buffer, "Before 😀\nPlease review this note.\nPlease send me the final version.\nAfter untouched", -1);
    gtk_widget_set_size_request(view, -1, 100);
    report_multiline = view;
    gtk_box_append(GTK_BOX(box), view);
    const char *names[] = {"fixture-tagged", "fixture-object", "fixture-readonly"};
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        GtkWidget *excluded = gtk_text_view_new();
        excluded_views[i] = excluded;
        gtk_widget_set_name(excluded, names[i]);
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
        gtk_box_append(GTK_BOX(box), excluded);
    }
    g_signal_connect(window, "close-request", G_CALLBACK(close_fixture), loop);
    gtk_window_present(GTK_WINDOW(window));
    if (g_getenv("TYPELAY_FIXTURE_FULLSCREEN")) gtk_window_fullscreen(GTK_WINDOW(window));
    gtk_widget_grab_focus(first);
    gtk_editable_set_position(GTK_EDITABLE(first), -1);
    if (g_getenv("TYPELAY_FIXTURE_TWO_WINDOWS")) {
        other_window = gtk_window_new();
        gtk_window_set_title(GTK_WINDOW(other_window), "TypeLay GTK4 second window");
        gtk_window_set_default_size(GTK_WINDOW(other_window), 600, 300);
        GtkWidget *other_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
        gtk_widget_set_margin_start(other_box, 32); gtk_widget_set_margin_end(other_box, 32);
        other_entry = gtk_entry_new();
        gtk_widget_set_name(other_entry, "fixture-other-window");
        gtk_editable_set_text(GTK_EDITABLE(other_entry), "The other window is unchanged.");
        gtk_box_append(GTK_BOX(other_box), other_entry);
        gtk_window_set_child(GTK_WINDOW(other_window), other_box);
        g_signal_connect(other_window, "close-request", G_CALLBACK(close_fixture), loop);
        gtk_window_present(GTK_WINDOW(other_window));
    }
    report_path = g_getenv("TYPELAY_FIXTURE_STATE");
    if (report_path && g_path_is_absolute(report_path)) {
        report_window = window;
        report_first = first;
        report_second = second;
        g_signal_connect(window, "notify::focus-widget", G_CALLBACK(observe_focus), NULL);
        GtkWidget *key_clients[] = {entry_client(first), entry_client(second), view};
        for (guint index = 0; index < G_N_ELEMENTS(key_clients); index++) {
            GtkEventController *keys = gtk_event_controller_legacy_new();
            gtk_event_controller_set_propagation_phase(keys, GTK_PHASE_CAPTURE);
            g_signal_connect(keys, "event", G_CALLBACK(observe_key), NULL);
            gtk_widget_add_controller(key_clients[index], keys);
        }
        watch_entry(first, &first_evidence);
        watch_entry(second, &second_evidence);
        motion_init();
        g_timeout_add(25, report_state, NULL);
    }
    g_main_loop_run(loop);
    glossary_capture_clear();
    g_main_loop_unref(loop);
    g_free(last_report);
    g_free(motion_mode); g_free(motion_body);
    if (motion_frames) json_array_unref(motion_frames);
    for (guint index = 0; index < G_N_ELEMENTS(focus_transitions); index++)
        if (focus_transitions[index]) json_object_unref(focus_transitions[index]);
    for (guint index = 0; index < language_transition_count; index++)
        json_object_unref(language_transitions[index]);
    if (first_insert_context) json_object_unref(first_insert_context);
    if (first_delete_context) json_object_unref(first_delete_context);
    return 0;
}
