/* Test-host callbacks only. A bounded nested dispatch lets the public native
 * socket apply configuration while a real GtkEntry mutation callback is active. */
static gboolean reentry_fired;
static void mutation_barrier(const char *stage) {
    const char *directory = g_getenv("TYPELAY_FIXTURE_REENTRY");
    if (reentry_fired || !directory || !g_path_is_absolute(directory)
        || g_strcmp0(stage, g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE")) != 0) return;
    char *arm = g_build_filename(directory, "arm", NULL);
    gboolean armed = g_file_test(arm, G_FILE_TEST_EXISTS);
    g_free(arm);
    if (!armed) return;
    reentry_fired = TRUE;
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(report_first), &start, &end);
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "stage", stage);
    json_object_set_string_member(value, "text",
        gtk_entry_buffer_get_text(gtk_entry_get_buffer(GTK_ENTRY(report_first))));
    json_object_set_int_member(value, "start", start);
    json_object_set_int_member(value, "end", end);
    json_object_set_int_member(value, "insertions", first_evidence.insertions);
    json_object_set_int_member(value, "deletions", first_evidence.deletions);
    JsonNode *node = json_node_new(JSON_NODE_OBJECT);
    json_node_take_object(node, value);
    JsonGenerator *generator = json_generator_new();
    json_generator_set_root(generator, node);
    char *reached = g_build_filename(directory, "reached.json", NULL);
    char *serialized = json_generator_to_data(generator, NULL);
    g_file_set_contents(reached, serialized, -1, NULL);
    g_free(serialized); g_free(reached); g_object_unref(generator); json_node_free(node);
    char *release = g_build_filename(directory, "release", NULL);
    gint64 deadline = g_get_monotonic_time() + 2 * G_TIME_SPAN_SECOND;
    while (!g_file_test(release, G_FILE_TEST_EXISTS) && g_get_monotonic_time() < deadline) {
        g_main_context_iteration(NULL, FALSE);
        g_usleep(1000);
    }
    if (!g_file_test(release, G_FILE_TEST_EXISTS)) {
        char *timeout = g_build_filename(directory, "timeout", NULL);
        g_file_set_contents(timeout, stage, -1, NULL);
        g_free(timeout);
    }
    g_free(release);
}
static void selection_changed(GObject *object, GParamSpec *spec, gpointer data) {
    (void)object; (void)spec;
    if (data != &first_evidence || reentry_fired) return;
    const char *replacement = g_getenv("TYPELAY_FIXTURE_REENTRY_TEXT");
    if (!replacement || g_strcmp0(gtk_entry_buffer_get_text(
            gtk_entry_get_buffer(GTK_ENTRY(report_first))), replacement) != 0) return;
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(report_first), &start, &end);
    if (start == g_utf8_strlen(replacement, -1) && start == end)
        mutation_barrier("selection");
}
static void footer_changed(GObject *object, GParamSpec *spec, gpointer data) {
    (void)object; (void)spec; (void)data;
    const char *directory = g_getenv("TYPELAY_FIXTURE_REENTRY");
    if (!directory || reentry_fired) return;
    char *arm = g_build_filename(directory, "arm", NULL);
    gboolean armed = g_file_test(arm, G_FILE_TEST_EXISTS);
    g_free(arm);
    if (!armed) return;
    reentry_fired = TRUE;
    /* Ordinary application focus change synchronously hides the source overlay.
       No nested socket request: its read source is already dispatching render. */
    gtk_widget_grab_focus(report_second);
    char *fired = g_build_filename(directory, "footer-fired", NULL);
    g_file_set_contents(fired, "focused-second", -1, NULL);
    g_free(fired);
}
static void expand_changed(GObject *object, GParamSpec *spec, gpointer data) {
    (void)object; (void)spec; (void)data;
    mutation_barrier("expand");
    const char *directory = g_getenv("TYPELAY_FIXTURE_REENTRY");
    if (directory && reentry_fired) {
        char *returned = g_build_filename(directory, "expand-returned", NULL);
        g_file_set_contents(returned, "returning-from-notify", -1, NULL);
        g_free(returned);
    }
}
static void watch_session_button(GtkWidget *widget) {
    const char *directory = g_getenv("TYPELAY_FIXTURE_REENTRY");
    const char *stage = g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE");
    gboolean expand = g_strcmp0(stage, "expand") == 0;
    if (!directory || (!expand && g_strcmp0(stage, "footer") != 0)
        || !GTK_IS_BUTTON(widget) || !gtk_widget_get_mapped(widget)
        || g_strcmp0(gtk_button_get_label(GTK_BUTTON(widget)), expand ? "展开" : "fixture-initial-exit") != 0
        || g_object_get_data(G_OBJECT(widget), "fixture-button-watched")) return;
    g_object_set_data(G_OBJECT(widget), "fixture-button-watched", GINT_TO_POINTER(1));
    g_signal_connect(widget, "notify::label", expand ? G_CALLBACK(expand_changed) : G_CALLBACK(footer_changed), NULL);
    char *ready = g_build_filename(directory, expand ? "expand-ready" : "footer-ready", NULL);
    g_file_set_contents(ready, "watching-label", -1, NULL);
    g_free(ready);
}
#if GTK_MAJOR_VERSION == 4
static void remove_session_controller(GtkWidget *widget, gboolean editor) {
    const char *directory = g_getenv("TYPELAY_FIXTURE_REENTRY");
    if (!directory || reentry_fired || g_strcmp0(g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE"),
            editor ? "controller-editor" : "controller-source") != 0) return;
    char *arm = g_build_filename(directory, "arm", NULL);
    gboolean armed = g_file_test(arm, G_FILE_TEST_EXISTS);
    g_free(arm);
    if (!armed) return;
    reentry_fired = TRUE;
    /* Inspect public GTK controllers, not the production module's qdata. */
    GListModel *controllers = gtk_widget_observe_controllers(widget);
    guint removed = 0;
    for (guint index = 0; index < g_list_model_get_n_items(controllers); index++) {
        GtkEventController *controller = g_list_model_get_item(controllers, index);
        if (GTK_IS_EVENT_CONTROLLER_LEGACY(controller)
            && gtk_event_controller_get_propagation_phase(controller) == GTK_PHASE_CAPTURE) {
            gtk_widget_remove_controller(widget, controller);
            removed++;
            g_object_unref(controller);
            break;
        }
        g_object_unref(controller);
    }
    g_object_unref(controllers);
    char *path = g_build_filename(directory, "controller-removed", NULL);
    char *count = g_strdup_printf("%u", removed);
    g_file_set_contents(path, count, -1, NULL);
    g_free(count); g_free(path);
}
#endif
