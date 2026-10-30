/* Shared host operations/observations for real GTK3/GTK4 source glossary tests.
 * Commands touch fixture widgets. The synchronous More reentry case wraps the
 * real provider observation without altering its return value or IME state. */
static guint glossary_command, glossary_insertions, glossary_deletions;
static gboolean glossary_multiline;
static void glossary_inserted(GtkTextBuffer *buffer, GtkTextIter *position, gchar *text, gint length, gpointer data) {
    (void)buffer; (void)position; (void)text; (void)length; (void)data; glossary_insertions++;
}
static void glossary_deleted(GtkTextBuffer *buffer, GtkTextIter *start, GtkTextIter *end, gpointer data) {
    (void)buffer; (void)start; (void)end; (void)data; glossary_deletions++;
}
static void glossary_watch_buffer(GtkTextBuffer *buffer) {
    if (g_object_get_data(G_OBJECT(buffer), "fixture-glossary-watched")) return;
    g_object_set_data(G_OBJECT(buffer), "fixture-glossary-watched", GINT_TO_POINTER(1));
    g_signal_connect(buffer, "insert-text", G_CALLBACK(glossary_inserted), NULL);
    g_signal_connect(buffer, "delete-range", G_CALLBACK(glossary_deleted), NULL);
}
static char *glossary_body(GtkTextBuffer *buffer) {
    GtkTextIter start, end;
    gtk_text_buffer_get_bounds(buffer, &start, &end);
    return gtk_text_buffer_get_text(buffer, &start, &end, TRUE);
}
static GtkWidget *saved_more_button;
#if GTK_MAJOR_VERSION == 3
/* event-after is emitted after the specific button handler, even when that
 * handler consumes the event. This observes only the real fixture TextView;
 * it neither consumes input nor changes focus, selection or provider state. */
static guint glossary_source_release_sequence, glossary_source_release_instance;
static gint64 glossary_source_released_at;
static gboolean glossary_source_release_focused;
static void glossary_source_event_after(GtkWidget *widget, GdkEvent *event, gpointer unused) {
    (void)unused;
    if (widget != report_multiline || event->type != GDK_BUTTON_RELEASE
        || event->button.button != GDK_BUTTON_PRIMARY || event->any.send_event
        || gtk_get_event_widget(event) != widget) return;
    glossary_source_release_sequence++;
    glossary_source_release_instance = language_instance(G_OBJECT(widget));
    glossary_source_released_at = g_get_monotonic_time() / 1000;
    glossary_source_release_focused = gtk_widget_has_focus(widget);
}
static void glossary_report_source_pointer(JsonBuilder *builder) {
    if (!g_object_get_data(G_OBJECT(report_multiline), "fixture-glossary-release-watched")) {
        g_object_set_data(G_OBJECT(report_multiline), "fixture-glossary-release-watched", GINT_TO_POINTER(1));
        g_signal_connect(report_multiline, "event-after", G_CALLBACK(glossary_source_event_after), NULL);
    }
    json_builder_set_member_name(builder, "instance");
    json_builder_add_int_value(builder, language_instance(G_OBJECT(report_multiline)));
    json_builder_set_member_name(builder, "pointerRelease");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "sequence");
    json_builder_add_int_value(builder, glossary_source_release_sequence);
    json_builder_set_member_name(builder, "instance");
    json_builder_add_int_value(builder, glossary_source_release_instance);
    json_builder_set_member_name(builder, "atMs");
    json_builder_add_int_value(builder, glossary_source_released_at);
    json_builder_set_member_name(builder, "focused");
    json_builder_add_boolean_value(builder, glossary_source_release_focused);
    json_builder_end_object(builder);
}
static void glossary_find_child(GtkWidget *widget, gpointer data);
static GtkWidget *find_named_widget(GtkWidget *widget, const char *name) {
    if (g_strcmp0(gtk_widget_get_name(widget), name) == 0) return widget;
    gpointer search[] = {(gpointer)name, NULL};
    if (GTK_IS_CONTAINER(widget)) gtk_container_forall(GTK_CONTAINER(widget), glossary_find_child, search);
    return search[1];
}
static void glossary_find_child(GtkWidget *widget, gpointer data) {
    gpointer *search = data;
    if (!search[1]) search[1] = find_named_widget(widget, search[0]);
}
#endif
/* Synchronous fixture-only dependency reentry: delegate the real provider,
 * never manufacture Clear. Install/restore in one public button command; no
 * timeout or later observation may consume the selected probe. */
typedef struct {
    GtkIMContext *context;
    GtkWidget *client, *trigger;
    const TlFcitxCapabilityV1 *original;
    TlFcitxCapabilityV1 wrapper;
    guint probes, fire_at;
    gboolean inspecting, armed, fired, refuse;
} MoreProbeReentry;
static MoreProbeReentry *more_probe_active;
static guint more_probe_count, more_probe_expected, more_probe_before, more_probe_after;
static gboolean more_probe_fired, more_probe_visible;
static gboolean more_probe_inspect(GtkIMContext *context, GtkWidget *widget, TlFcitxStateV1 *state) {
    MoreProbeReentry *probe = more_probe_active;
    if (!probe) return FALSE;
    if (probe->inspecting) return probe->original->inspect(context, widget, state);
    probe->inspecting = TRUE;
    gboolean valid = probe->original->inspect(context, widget, state);
    probe->inspecting = FALSE;
    probe->probes++;
    if (probe->armed && probe->probes == probe->fire_at && valid && ime_clear(state)
        && context == probe->context && widget == probe->client
        && g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1) == &probe->wrapper) {
        probe->armed = FALSE;
        probe->fired = TRUE;
        if (probe->refuse) return FALSE; /* Withdraw, never manufacture Clear. */
        /* Restore before public close/reopen so reentrant observers always use
         * the real provider. These buttons do not focus another input widget. */
        g_object_set_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1, (gpointer)probe->original);
        g_signal_emit_by_name(probe->trigger, "clicked");
        g_signal_emit_by_name(probe->trigger, "clicked");
    }
    return valid;
}
static void probe_more_action(const char *name, guint fire_at, gboolean refuse) {
    GtkWidget *button = find_named_widget(report_window, name);
    GtkWidget *trigger = find_named_widget(report_window, "typelay-more");
    GtkWidget *popup = find_named_widget(report_window, "typelay-more-popup");
    GtkWidget *client = glossary_multiline ? report_multiline : entry_client(report_first);
    GtkIMContext *context = g_object_get_data(G_OBJECT(client), TL_FCITX_CONTEXT_V1);
    if (more_probe_active || !button || !trigger || !popup || !GTK_IS_BUTTON(button)
        || !GTK_IS_BUTTON(trigger) || !gtk_widget_get_mapped(button)
        || !GTK_IS_IM_CONTEXT(context)) g_error("Expected current More and real source IM context");
    const TlFcitxCapabilityV1 *cap = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    if (!cap || cap->size != sizeof(*cap) || cap->version != TL_FCITX_ABI_V1 || !cap->inspect)
        g_error("Expected real source IM capability");
    g_object_ref(button); g_object_ref(popup); g_object_ref(trigger);
    g_object_ref(client); g_object_ref(context);
    MoreProbeReentry probe = {
        .context = context, .client = client, .trigger = trigger, .original = cap,
        .wrapper = *cap, .fire_at = fire_at, .armed = TRUE, .refuse = refuse
    };
    probe.wrapper.inspect = more_probe_inspect;
    more_probe_before = language_instance(G_OBJECT(popup));
    more_probe_active = &probe;
    g_object_set_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1, &probe.wrapper);
    g_signal_emit_by_name(button, "clicked");
    if (g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1) == &probe.wrapper)
        g_object_set_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1, (gpointer)cap);
    more_probe_active = NULL;
    GtkWidget *replacement = find_named_widget(report_window, "typelay-more-popup");
    more_probe_count = probe.probes; more_probe_expected = probe.fire_at;
    more_probe_fired = probe.fired;
    more_probe_after = replacement ? language_instance(G_OBJECT(replacement)) : 0;
    more_probe_visible = replacement && gtk_widget_get_visible(replacement);
    g_object_unref(context); g_object_unref(client); g_object_unref(trigger);
    g_object_unref(popup); g_object_unref(button);
    if (!probe.fired || probe.probes != probe.fire_at)
        g_error("The selected real Clear More probe did not execute");
}

static void glossary_host_command(void) {
    const char *path = g_getenv("TYPELAY_FIXTURE_GLOSSARY_COMMAND");
    char *body = NULL;
    if (!path || !g_file_get_contents(path, &body, NULL, NULL)) return;
    guint serial = 0;
    char action[32] = {0};
    if (sscanf(body, "%u %31s", &serial, action) == 2 && serial > glossary_command) {
        GtkTextBuffer *text_buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(report_multiline));
        glossary_watch_buffer(text_buffer);
        if (g_str_equal(action, "prepare-textview")) {
            glossary_multiline = TRUE;
            gtk_text_buffer_set_text(text_buffer, "one 😀 PR\nnext line", -1);
            GtkTextIter start, end;
            gtk_text_buffer_get_iter_at_offset(text_buffer, &start, 4);
            gtk_text_buffer_get_iter_at_offset(text_buffer, &end, 8);
            gtk_text_buffer_select_range(text_buffer, &start, &end);
        } else if (g_str_equal(action, "prepare-entry")) {
            glossary_multiline = FALSE;
            gtk_editable_set_position(GTK_EDITABLE(report_first), -1);
        } else if (g_str_equal(action, "probe-more-reopen")) {
            probe_more_action("typelay-glossary-add", 2u, FALSE);
        } else if (g_str_equal(action, "probe-more-refused")) {
            probe_more_action("typelay-regenerate", 1u, TRUE);
        } else if (g_str_equal(action, "probe-more-action-reopen")) {
            probe_more_action("typelay-regenerate", 1u, FALSE);
        } else if (g_str_equal(action, "retain-more-button")) {
            GtkWidget *button = find_named_widget(report_window, "typelay-glossary-add");
            if (!button || !GTK_IS_BUTTON(button) || !gtk_widget_get_mapped(button))
                g_error("Expected the current visible more-menu button");
            g_set_object(&saved_more_button, button);
        } else if (g_str_equal(action, "activate-retired-more")) {
            if (!saved_more_button || gtk_widget_get_mapped(saved_more_button))
                g_error("Expected a retired more-menu button");
            /* Public widget activation is deliberately NOT pointer evidence.
             * This negative checks a retained old instance cannot act anew. */
#if GTK_MAJOR_VERSION == 3
            gtk_button_clicked(GTK_BUTTON(saved_more_button));
#else
            g_signal_emit_by_name(saved_more_button, "clicked");
#endif
            g_clear_object(&saved_more_button);
        } else if (g_str_equal(action, "capture-more")) {
            GtkWidget *popup = find_named_widget(report_window, "typelay-more-popup");
#if GTK_MAJOR_VERSION == 3
            if (!popup || !gtk_widget_is_drawable(popup)) g_error("Expected a mapped more popup");
            gint width = gtk_widget_get_allocated_width(popup), height = gtk_widget_get_allocated_height(popup);
            if (width <= 0 || height <= 0) g_error("More popup allocation missing");
            cairo_surface_t *surface = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, width, height);
            cairo_t *cr = cairo_create(surface);
            /* Real current GtkWidget draw, not a compositor screenshot. The
             * surrounding test separately requires mapped/painted input hits. */
            gtk_widget_draw(popup, cr);
            cairo_destroy(cr);
            char *image = g_strdup_printf("%s.more.png", path);
            cairo_status_t status = cairo_surface_write_to_png(surface, image);
            cairo_surface_destroy(surface); g_free(image);
            if (status != CAIRO_STATUS_SUCCESS) g_error("Cannot capture the current more popup");
#else
            if (!popup || !gtk_widget_get_mapped(popup)) g_error("Expected a mapped more popup");
            gint width = gtk_widget_get_width(popup), height = gtk_widget_get_height(popup);
            if (width <= 0 || height <= 0) g_error("More popup allocation missing");
            GdkPaintable *paintable = gtk_widget_paintable_new(popup);
            GtkSnapshot *snapshot = gtk_snapshot_new();
            /* Public snapshot of this real mapped widget, not a compositor
             * screenshot or proof of physical input delivery. */
            gdk_paintable_snapshot(paintable, GDK_SNAPSHOT(snapshot), width, height);
            GskRenderNode *node = gtk_snapshot_free_to_node(snapshot);
            GskRenderer *renderer = gtk_native_get_renderer(gtk_widget_get_native(popup));
            if (!node || !renderer) g_error("More popup snapshot missing");
            GdkTexture *texture = gsk_renderer_render_texture(renderer, node, NULL);
            char *image = g_strdup_printf("%s.more.png", path);
            gboolean saved = texture && gdk_texture_save_to_png(texture, image);
            g_free(image); g_clear_object(&texture);
            gsk_render_node_unref(node); g_object_unref(paintable);
            if (!saved) g_error("Cannot capture the current more popup");
#endif
        } else if (g_str_equal(action, "reset-inactive-term")) {
            GtkWidget *term = find_named_widget(report_window, "typelay-glossary-source");
            GtkWidget *client = term && GTK_IS_ENTRY(term) ? entry_client(term) : NULL;
            if (!client || gtk_widget_has_focus(client)) g_error("Expected inactive glossary term");
            GtkIMContext *context = g_object_get_data(G_OBJECT(client), TL_FCITX_CONTEXT_V1);
            if (context && GTK_IS_IM_CONTEXT(context)) gtk_im_context_reset(context);
            else g_error("No real inactive term IM context");
        } else if (g_str_equal(action, "change-selection")) {
            if (glossary_multiline) {
                GtkTextIter start;
                gtk_text_buffer_get_start_iter(text_buffer, &start);
                gtk_text_buffer_place_cursor(text_buffer, &start);
            } else gtk_editable_select_region(GTK_EDITABLE(report_first), 0, 1);
        } else if (g_str_equal(action, "change-source") || g_str_equal(action, "replace-buffer")) {
            gboolean replace = g_str_equal(action, "replace-buffer");
            if (glossary_multiline) {
                char *text = glossary_body(text_buffer);
                if (replace) {
                    GtkTextIter insert, bound;
                    gtk_text_buffer_get_iter_at_mark(text_buffer, &insert, gtk_text_buffer_get_insert(text_buffer));
                    gtk_text_buffer_get_iter_at_mark(text_buffer, &bound, gtk_text_buffer_get_selection_bound(text_buffer));
                    gint insert_offset = gtk_text_iter_get_offset(&insert), bound_offset = gtk_text_iter_get_offset(&bound);
                    GtkTextBuffer *replacement = gtk_text_buffer_new(NULL);
                    gtk_text_buffer_set_text(replacement, text, -1);
                    gtk_text_buffer_get_iter_at_offset(replacement, &insert, insert_offset);
                    gtk_text_buffer_get_iter_at_offset(replacement, &bound, bound_offset);
                    gtk_text_buffer_select_range(replacement, &insert, &bound);
                    glossary_watch_buffer(replacement);
                    gtk_text_view_set_buffer(GTK_TEXT_VIEW(report_multiline), replacement);
                    g_object_unref(replacement);
                } else {
                    GtkTextIter end;
                    gtk_text_buffer_get_end_iter(text_buffer, &end);
                    gtk_text_buffer_insert(text_buffer, &end, " changed", -1);
                }
                g_free(text);
            } else {
                GtkEntryBuffer *buffer = gtk_entry_get_buffer(GTK_ENTRY(report_first));
                const char *text = gtk_entry_buffer_get_text(buffer);
                if (replace) {
                    gint position = gtk_editable_get_position(GTK_EDITABLE(report_first)), bound = 0;
                    g_object_get(report_first, "selection-bound", &bound, NULL);
                    GtkEntryBuffer *replacement = gtk_entry_buffer_new(text, -1);
                    gtk_entry_set_buffer(GTK_ENTRY(report_first), replacement);
                    g_signal_connect_after(replacement, "inserted-text", G_CALLBACK(entry_inserted), &first_evidence);
                    g_signal_connect_after(replacement, "deleted-text", G_CALLBACK(entry_deleted), &first_evidence);
                    gtk_editable_select_region(GTK_EDITABLE(report_first), bound, position);
                    g_object_unref(replacement);
                } else {
                    char *changed = g_strconcat(text, " changed", NULL);
                    gtk_entry_buffer_set_text(buffer, changed, -1);
                    g_free(changed);
                }
            }
        } else g_error("Unknown glossary fixture command");
        glossary_command = serial;
    }
    g_free(body);
}
static void glossary_report(JsonBuilder *builder) {
    if (!g_getenv("TYPELAY_FIXTURE_GLOSSARY_COMMAND")) return;
    json_builder_set_member_name(builder, "moreProbeReentry"); json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "probes"); json_builder_add_int_value(builder, more_probe_count);
    json_builder_set_member_name(builder, "expected"); json_builder_add_int_value(builder, more_probe_expected);
    json_builder_set_member_name(builder, "fired"); json_builder_add_boolean_value(builder, more_probe_fired);
    json_builder_set_member_name(builder, "before"); json_builder_add_int_value(builder, more_probe_before);
    json_builder_set_member_name(builder, "after"); json_builder_add_int_value(builder, more_probe_after);
    json_builder_set_member_name(builder, "visible"); json_builder_add_boolean_value(builder, more_probe_visible);
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "glossaryCommand");
    json_builder_add_int_value(builder, glossary_command);
    json_builder_set_member_name(builder, "glossarySource");
    json_builder_begin_object(builder);
#if GTK_MAJOR_VERSION == 3
    glossary_report_source_pointer(builder);
#endif
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(report_multiline));
    char *text = glossary_body(buffer);
    GtkTextIter start, end;
    gtk_text_buffer_get_selection_bounds(buffer, &start, &end);
    json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, text);
    g_free(text);
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&start));
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&end));
    json_builder_set_member_name(builder, "insertions"); json_builder_add_int_value(builder, glossary_insertions);
    json_builder_set_member_name(builder, "deletions"); json_builder_add_int_value(builder, glossary_deletions);
    json_builder_set_member_name(builder, "focused"); json_builder_add_boolean_value(builder, gtk_widget_has_focus(report_multiline));
    TlFcitxStateV1 state = {.size = sizeof state, .version = TL_FCITX_ABI_V1};
    gboolean valid = inspect_ime(report_multiline, &state);
    json_builder_set_member_name(builder, "imeClear"); json_builder_add_boolean_value(builder, valid && ime_clear(&state));
    json_builder_set_member_name(builder, "ime"); json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "context"); json_builder_add_int_value(builder, valid ? state.context_generation : 0);
    json_builder_end_object(builder);
    json_builder_end_object(builder);
}
