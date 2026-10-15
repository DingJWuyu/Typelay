/* Bounded observations of real GTK frame allocations in the isolated fixture.
 * The sidecar changes this process's GtkSettings only, never desktop settings. */
static const char *motion_path;
static char *motion_mode, *motion_body;
static JsonArray *motion_frames;
static guint motion_tick_id, motion_identity;
static gboolean motion_original_animations, motion_reduced_supported;
static gint motion_original_reduced;

static guint motion_id(GObject *object) {
    guint value = GPOINTER_TO_UINT(g_object_get_data(object, "fixture-motion-id"));
    if (!value) {
        value = ++motion_identity;
        g_object_set_data(object, "fixture-motion-id", GUINT_TO_POINTER(value));
    }
    return value;
}
static gint motion_height(GtkWidget *widget) {
#if GTK_MAJOR_VERSION == 3
    return gtk_widget_get_allocated_height(widget);
#else
    return gtk_widget_get_height(widget);
#endif
}
static void motion_controls(GtkWidget *widget, gpointer data) {
    gboolean *visible = data;
    const char *name = gtk_widget_get_name(widget);
    if (g_strcmp0(name, "typelay-header") == 0) visible[0] = gtk_widget_get_mapped(widget);
    if (g_strcmp0(name, "typelay-footer") == 0) visible[1] = gtk_widget_get_mapped(widget);
#if GTK_MAJOR_VERSION == 3
    if (GTK_IS_CONTAINER(widget)) gtk_container_forall(GTK_CONTAINER(widget), motion_controls, data);
#else
    for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child))
        motion_controls(child, data);
#endif
}
static gboolean motion_tick(GtkWidget *widget, GdkFrameClock *clock, gpointer unused) {
    (void)clock; (void)unused;
    gint64 now = g_get_monotonic_time() / 1000;
    guint count = json_array_get_length(motion_frames);
    /* At most one observation per 25ms, independent of monitor refresh rate. */
    if (count && now - json_object_get_int_member(json_array_get_object_element(motion_frames, count - 1),
            "atMonoMs") < 25) return G_SOURCE_CONTINUE;
    GtkWidget *candidate = NULL;
    find_candidate(widget, &candidate);
    JsonObject *frame = json_object_new();
    json_object_set_int_member(frame, "atMonoMs", now);
    if (candidate && gtk_widget_get_mapped(candidate)) {
        GtkWidget *scroll = gtk_widget_get_ancestor(candidate, GTK_TYPE_SCROLLED_WINDOW);
        GtkWidget *shell = gtk_widget_get_ancestor(candidate, GTK_TYPE_POPOVER);
        GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(candidate));
        GtkTextIter start, end;
        gtk_text_buffer_get_selection_bounds(buffer, &start, &end);
        json_object_set_int_member(frame, "start", gtk_text_iter_get_offset(&start));
        json_object_set_int_member(frame, "end", gtk_text_iter_get_offset(&end));
        json_object_set_boolean_member(frame, "focused", gtk_widget_has_focus(candidate));
        json_object_set_int_member(frame, "editor", motion_id(G_OBJECT(candidate)));
        json_object_set_int_member(frame, "buffer", motion_id(G_OBJECT(buffer)));
        json_object_set_int_member(frame, "scroll", motion_id(G_OBJECT(scroll)));
        json_object_set_int_member(frame, "shell", motion_id(G_OBJECT(shell)));
        json_object_set_int_member(frame, "height", motion_height(scroll));
        json_object_set_int_member(frame, "shellHeight", motion_height(shell));
        GtkAdjustment *adjustment = gtk_scrolled_window_get_vadjustment(GTK_SCROLLED_WINDOW(scroll));
        json_object_set_boolean_member(frame, "scrollable",
            gtk_adjustment_get_upper(adjustment) > gtk_adjustment_get_page_size(adjustment));
        gboolean visible[2] = {FALSE, FALSE};
        motion_controls(shell, visible);
        json_object_set_boolean_member(frame, "header", visible[0]);
        json_object_set_boolean_member(frame, "footer", visible[1]);
        gtk_text_buffer_get_bounds(buffer, &start, &end);
        char *body = gtk_text_buffer_get_text(buffer, &start, &end, TRUE);
        if (!motion_body) motion_body = g_strdup(body);
        json_object_set_boolean_member(frame, "textMatches", g_strcmp0(body, motion_body) == 0);
        g_free(body);
    }
    json_array_add_object_element(motion_frames, frame);
    if (json_array_get_length(motion_frames) < 64) return G_SOURCE_CONTINUE;
    motion_tick_id = 0;
    return G_SOURCE_REMOVE;
}
static void motion_report(JsonBuilder *builder) {
    if (!motion_path) return;
    char *mode = NULL;
    g_file_get_contents(motion_path, &mode, NULL, NULL);
    if (g_strcmp0(mode, motion_mode) != 0) {
        if (motion_tick_id) gtk_widget_remove_tick_callback(report_window, motion_tick_id);
        motion_tick_id = 0;
        g_free(motion_mode); motion_mode = g_strdup(mode);
        g_clear_pointer(&motion_body, g_free);
        if (motion_frames) json_array_unref(motion_frames);
        motion_frames = json_array_new();
        GtkSettings *settings = gtk_widget_get_settings(report_window);
        g_object_set(settings, "gtk-enable-animations",
            mode ? !g_str_has_prefix(mode, "disabled") : motion_original_animations, NULL);
        if (motion_reduced_supported)
            g_object_set(settings, "gtk-interface-reduced-motion",
                mode ? (g_str_has_prefix(mode, "reduced") ? 1 : 0) : motion_original_reduced, NULL);
        if (mode) motion_tick_id = gtk_widget_add_tick_callback(report_window, motion_tick, NULL, NULL);
    }
    g_free(mode);
    GtkSettings *settings = gtk_widget_get_settings(report_window);
    gboolean enabled = FALSE;
    gint reduced = 0;
    g_object_get(settings, "gtk-enable-animations", &enabled, NULL);
    if (motion_reduced_supported)
        g_object_get(settings, "gtk-interface-reduced-motion", &reduced, NULL);
    json_builder_set_member_name(builder, "motionReducedSupported"); json_builder_add_boolean_value(builder, motion_reduced_supported);
    json_builder_set_member_name(builder, "motionText"); json_builder_add_string_value(builder, motion_body ? motion_body : "");
    json_builder_set_member_name(builder, "motionAnimations"); json_builder_add_boolean_value(builder, enabled);
    json_builder_set_member_name(builder, "motionReduced"); json_builder_add_int_value(builder, reduced);
    json_builder_set_member_name(builder, "motionMode");
    json_builder_add_string_value(builder, motion_mode ? motion_mode : "");
    json_builder_set_member_name(builder, "motionFrames");
    JsonNode *node = json_node_new(JSON_NODE_ARRAY);
    json_node_set_array(node, motion_frames);
    json_builder_add_value(builder, node);
}
static void motion_init(void) {
    motion_path = g_getenv("TYPELAY_FIXTURE_MOTION");
    if (!motion_path) return;
    g_assert(g_path_is_absolute(motion_path));
    motion_frames = json_array_new();
    GtkSettings *settings = gtk_widget_get_settings(report_window);
    motion_reduced_supported = g_object_class_find_property(G_OBJECT_GET_CLASS(settings), "gtk-interface-reduced-motion") != NULL;
    g_object_get(settings, "gtk-enable-animations", &motion_original_animations, NULL);
    if (motion_reduced_supported)
        g_object_get(settings, "gtk-interface-reduced-motion", &motion_original_reduced, NULL);
}
