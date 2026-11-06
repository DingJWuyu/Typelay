#ifndef TYPELAY_PRESENTATION_IDENTITY_H
#define TYPELAY_PRESENTATION_IDENTITY_H

#include <json-glib/json-glib.h>
#include <string.h>

/* Shared product identity is separate from native controls and action leases. */
typedef struct {
    char interaction[37];
    gint64 display, draft, action;
} TlPresentationIdentity;

static gboolean tl_presentation_guid(const char *value) {
    return value && g_uuid_string_is_valid(value)
        && strcmp(value, "00000000-0000-0000-0000-000000000000") != 0;
}
static gboolean tl_presentation_number(JsonObject *object, const char *key, gint64 minimum, gint64 *value) {
    JsonNode *node = object ? json_object_get_member(object, key) : NULL;
    if (!node || !JSON_NODE_HOLDS_VALUE(node) || json_node_get_value_type(node) != G_TYPE_INT64) return FALSE;
    *value = json_node_get_int(node);
    return *value >= minimum && *value <= G_GINT64_CONSTANT(9007199254740991);
}
static JsonObject *tl_presentation_form(JsonObject *view) {
    return tl_object(tl_object(view, "viewContext"), "glossaryForm");
}
static gboolean tl_presentation_read(JsonObject *view, TlPresentationIdentity *result) {
    JsonObject *context = tl_object(view, "viewContext"), *identity = tl_object(context, "identity");
    gint64 version;
    const char *interaction = tl_string(identity, "interactionId");
    if (!identity || json_object_get_size(identity) != 5 || !tl_presentation_guid(interaction)
        || !tl_presentation_number(identity, "version", 1, &version) || version != 1
        || !tl_presentation_number(identity, "displaySequence", 1, &result->display)
        || !tl_presentation_number(identity, "draftRevision", 0, &result->draft)
        || !tl_presentation_number(identity, "actionRevision", 1, &result->action)) return FALSE;
    JsonNode *node = json_object_get_member(context, "glossaryForm");
    if (node && !JSON_NODE_HOLDS_NULL(node)) {
        JsonObject *form = tl_presentation_form(view);
        JsonNode *current = form ? json_object_get_member(form, "current") : NULL;
        if (!form || json_object_get_size(form) != 2 || !tl_presentation_guid(tl_string(form, "formId"))
            || !current || !JSON_NODE_HOLDS_VALUE(current) || json_node_get_value_type(current) != G_TYPE_BOOLEAN) return FALSE;
    }
    g_strlcpy(result->interaction, interaction, sizeof result->interaction);
    return TRUE;
}
static gboolean tl_presentation_same_interaction(const TlPresentationIdentity *left, const TlPresentationIdentity *right) {
    return *left->interaction && *right->interaction && g_ascii_strcasecmp(left->interaction, right->interaction) == 0;
}
static gboolean tl_presentation_same_draft_action(const TlPresentationIdentity *left, const TlPresentationIdentity *right) {
    return tl_presentation_same_interaction(left, right) && left->draft == right->draft && left->action == right->action;
}
static gboolean tl_presentation_follows(JsonObject *previous, JsonObject *next,
    const TlPresentationIdentity *before, const TlPresentationIdentity *after) {
    if (after->action < before->action
        || (after->display == before->display && !tl_presentation_same_draft_action(before, after))) return FALSE;
    if (!tl_presentation_same_interaction(before, after)) return TRUE;
    JsonObject *before_token = tl_object(tl_object(previous, "viewContext"), "token");
    JsonObject *after_token = tl_object(tl_object(next, "viewContext"), "token");
    return after->draft >= before->draft
        && g_strcmp0(tl_string(previous, "state"), tl_string(next, "state")) == 0
        && ((!before_token && !after_token) || (before_token && after_token && json_object_equal(before_token, after_token)));
}
static gboolean tl_presentation_form_matches(JsonObject *view, const char *id) {
    return id && g_strcmp0(tl_string(tl_presentation_form(view), "formId"), id) == 0;
}

#endif
