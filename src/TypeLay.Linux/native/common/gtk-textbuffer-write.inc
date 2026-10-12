/* Private compile-time implementation shared by the GTK3 and GTK4 targets.
 * Include once, after the toolkit-specific target helpers. This is not a public
 * API or a runtime adapter: each module retains its native widget/IM lifecycle.
 * Required local contract: Target revisions and mutation state; standard_textview,
 * validate_ime_locally, same_state, inspect_ime, im_capability; metadata/focus checks;
 * read_text (UTF-16 selection), write_check and receipt. The caller has already
 * validated the full plan, original body/selection, actual Clear and deadline.
 * GTK3 hosts may implement undo through user-action signals; GTK4 also groups
 * its existing native history. Neither module enables or replaces host undo.
 */
typedef struct {
    Target *target;
    GtkWidget *widget;
    GtkTextBuffer *buffer;
    JsonObject *approved, *basis;
    guint64 storage, input, focus, content;
    TlFcitxStateV1 state;
} TextViewWrite;

static void textview_marks(GtkTextBuffer *buffer, gint *cursor, gint *bound) {
    GtkTextIter position;
    gtk_text_buffer_get_iter_at_mark(buffer, &position, gtk_text_buffer_get_insert(buffer));
    *cursor = gtk_text_iter_get_offset(&position);
    gtk_text_buffer_get_iter_at_mark(buffer, &position, gtk_text_buffer_get_selection_bound(buffer));
    *bound = gtk_text_iter_get_offset(&position);
}
static gboolean textview_body_matches(GtkTextBuffer *buffer, const char *expected) {
    GtkTextIter first, last;
    gtk_text_buffer_get_bounds(buffer, &first, &last);
    char *text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
    gboolean same = strcmp(text, expected) == 0;
    g_free(text);
    return same;
}
static gboolean textview_guard(TextViewWrite *write, const char *text, gint cursor, gint bound,
                               const char **failure) {
    Target *target = write->target;
    gboolean valid = write_check(!target->dead && target->widget == write->widget, "destroyed", failure)
        && write_check(!target->quarantined, "quarantined", failure)
        && write_check(target->buffer == G_OBJECT(write->buffer) && standard_textview(target), "buffer", failure)
        && write_check(target->storage_revision == write->storage && target->content_revision == write->content,
            "storage-revision", failure)
        && write_check(validate_ime_locally(target, &write->state), "im-local", failure)
        && write_check(target->ime_known && !target->composing && same_state(&target->im_state, &write->state),
            "im-state", failure)
        && write_check(target->input_revision == write->input, "input-revision", failure)
        && write_check(target->focus_revision == write->focus && tl_focused() == target, "focus", failure)
        && write_check(tl_metadata_matches(target, write->approved) && tl_metadata_matches(target, write->basis),
            "metadata", failure);
    if (!valid) return FALSE;
    gint actual_cursor, actual_bound;
    textview_marks(write->buffer, &actual_cursor, &actual_bound);
    return write_check(actual_cursor == cursor && actual_bound == bound, "selection", failure)
        && write_check(textview_body_matches(write->buffer, text), "body", failure);
}
static JsonObject *write_textview(Target *target, JsonObject *plan, JsonObject *basis, JsonObject *approved,
                                 const char *text, const char *after_delete, gint from, gint to,
                                 gint caret, gint64 until) {
    const char *replacement = tl_string(plan, "replacement"), *result = tl_string(plan, "resultText");
    TextViewWrite write = {
        .target = target, .widget = g_object_ref(target->widget), .buffer = g_object_ref(GTK_TEXT_BUFFER(target->buffer)),
        .approved = approved, .basis = basis, .storage = target->storage_revision,
        .input = target->input_revision, .focus = target->focus_revision, .content = target->content_revision,
        .state = target->im_state
    };
    gint cursor, bound;
    textview_marks(write.buffer, &cursor, &bound);
    const char *failure = NULL, *phase = "prepare";
    GtkIMContext *context = g_object_ref(target->im_context);
    const TlFcitxCapabilityV1 *capability = im_capability(context);
    gboolean ready = textview_guard(&write, text, cursor, bound, &failure)
        && write_check(tl_now() <= until, "expiry", &failure);
    if (ready) {
        /* TextView resets on insertion/selection mark movement. Move that
           ordinary reset before storage, only from the already proven Clear
           baseline; then establish a fresh remote barrier and original source. */
        target->im_probing = TRUE;
        gboolean prepared = capability && capability->prepare_caret(context, write.widget, &write.state);
        target->im_probing = FALSE;
        ready = write_check(prepared, "caret-reset", &failure)
            && write_check(!target->dead && target->im_context == context, "im-context", &failure)
            && write_check(inspect_ime(target) && target->ime_known && !target->composing, "im-unclear", &failure)
            && write_check(target->im_state.context_generation == write.state.context_generation,
                "im-context", &failure)
            && write_check(target->im_state.input_epoch == write.state.input_epoch, "im-input", &failure);
        if (ready) {
            write.state = target->im_state;
            ready = textview_guard(&write, text, cursor, bound, &failure)
                && write_check(tl_now() <= until, "expiry", &failure);
        }
    }
    g_object_unref(context);
    gboolean grouped = FALSE;
    if (ready) {
        phase = "begin-action";
        gtk_text_buffer_begin_user_action(write.buffer);
        grouped = TRUE;
        /* begin-user-action itself can invoke ordinary application callbacks.
           It establishes no authority and does not freeze the buffer. */
        ready = textview_guard(&write, text, cursor, bound, &failure)
            && write_check(tl_now() <= until, "expiry", &failure);
    }
    if (!ready) {
        if (grouped) gtk_text_buffer_end_user_action(write.buffer);
        if (g_getenv("TYPELAY_TRACE"))
            g_printerr("TypeLay GTK%d TextView guard phase=%s check=%s atMs=%" G_GINT64_FORMAT "\n",
                GTK_MAJOR_VERSION, phase, failure ? failure : "unknown", tl_now());
        g_object_unref(write.buffer); g_object_unref(write.widget);
        return receipt("RejectedBeforeMutation", tl_now() > until ? "StaleObservation" : "SourceChanged");
    }

    target->mutating = TRUE;
    tl_clear_permit();
    phase = "delete";
    if (to > from) {
        GtkTextIter first, last;
        gtk_text_buffer_get_iter_at_offset(write.buffer, &first, from);
        gtk_text_buffer_get_iter_at_offset(write.buffer, &last, to);
        gtk_text_buffer_delete(write.buffer, &first, &last);
        write.storage++;
    }
    gint deleted_cursor = cursor - CLAMP(cursor - from, 0, to - from);
    gint deleted_bound = bound - CLAMP(bound - from, 0, to - from);
    gboolean valid = textview_guard(&write, after_delete, deleted_cursor, deleted_bound, &failure);
    gint count = (gint)g_utf8_strlen(replacement, -1), final_position = from + count;
    if (valid) {
        phase = "insert";
        /* Any buffer mutation invalidates saved GtkTextIters. Rebuild only after
           the deletion receipt and exact body/local input proof have passed. */
        GtkTextIter at;
        gtk_text_buffer_get_iter_at_offset(write.buffer, &at, from);
        gboolean cursor_right = !gtk_text_mark_get_left_gravity(gtk_text_buffer_get_insert(write.buffer));
        gboolean bound_right = !gtk_text_mark_get_left_gravity(gtk_text_buffer_get_selection_bound(write.buffer));
        gint inserted_cursor = deleted_cursor + ((deleted_cursor > from || (deleted_cursor == from && cursor_right)) ? count : 0);
        gint inserted_bound = deleted_bound + ((deleted_bound > from || (deleted_bound == from && bound_right)) ? count : 0);
        gtk_text_buffer_insert(write.buffer, &at, replacement, -1);
        write.storage++;
        valid = textview_guard(&write, result, inserted_cursor, inserted_bound, &failure);
        if (valid) {
            phase = "caret";
            gtk_text_buffer_get_iter_at_offset(write.buffer, &at, final_position);
            gtk_text_buffer_place_cursor(write.buffer, &at);
            valid = textview_guard(&write, result, final_position, final_position, &failure);
            if (valid) {
                phase = "scroll";
                gtk_text_view_scroll_mark_onscreen(GTK_TEXT_VIEW(write.widget), gtk_text_buffer_get_insert(write.buffer));
                valid = textview_guard(&write, result, final_position, final_position, &failure);
            }
        }
    }
    /* Always balance the original application undo group. Its end signal can
       change the field too, so no Verified receipt precedes this callback. */
    gtk_text_buffer_end_user_action(write.buffer);
    if (valid) {
        phase = "end-action";
        valid = textview_guard(&write, result, final_position, final_position, &failure);
    }
    gint first = -1, last = -1;
    char *after = valid ? read_text(target, &first, &last) : NULL;
    gboolean verified = valid && write_check(after && strcmp(after, result) == 0, "body", &failure)
        && write_check(first == caret && last == caret, "selection", &failure);
    if (!verified && g_getenv("TYPELAY_TRACE"))
        g_printerr("TypeLay GTK%d TextView guard phase=%s check=%s atMs=%" G_GINT64_FORMAT "\n",
            GTK_MAJOR_VERSION, phase, failure ? failure : "unknown", tl_now());
    target->mutating = FALSE;
    target->content_revision++;
    JsonObject *reply = receipt(verified ? "Verified" : "OutcomeUnknown", verified ? "None" : "UnknownOutcome");
    if (verified) {
        JsonObject *snapshot = tl_copy(tl_object(plan, "expected"));
        json_object_set_string_member(snapshot, "text", after);
        JsonObject *selection = json_object_new();
        json_object_set_int_member(selection, "start", caret); json_object_set_int_member(selection, "end", caret);
        json_object_set_object_member(snapshot, "selection", selection);
        json_object_set_int_member(snapshot, "observedAtMs", tl_now());
        json_object_set_string_member(snapshot, "ime", "Clear");
        json_object_set_string_member(snapshot, "access", "Supported");
        json_object_set_object_member(reply, "verifiedSnapshot", snapshot);
    } else target->quarantined = TRUE;
    g_free(after); g_object_unref(write.buffer); g_object_unref(write.widget);
    return reply;
}
