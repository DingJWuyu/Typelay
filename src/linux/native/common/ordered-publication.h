#ifndef TYPELAY_GTK_ORDERED_PUBLICATION_H
#define TYPELAY_GTK_ORDERED_PUBLICATION_H

/* tl_copy owns the root but json_node_copy shares nested objects. Detach each
 * path before normalization so comparing a refresh never alters either live
 * publication, including the plan ID needed to finish write admission. */
static JsonObject *ordered_detach_object(JsonObject *parent, const char *member) {
    JsonObject *object = tl_object(parent, member);
    if (!object) return NULL;
    JsonObject *copy = tl_copy(object);
    json_object_set_object_member(parent, member, copy);
    return copy;
}
static void normalize_ordered_publication(JsonObject *value) {
    JsonObject *basis = ordered_detach_object(value, "authority");
    JsonObject *stamp = ordered_detach_object(basis, "contextStamp");
    JsonObject *permit = ordered_detach_object(value, "permit");
    JsonObject *plan = ordered_detach_object(permit, "plan");
    JsonObject *expected = ordered_detach_object(plan, "expected");
    JsonObject *lease = ordered_detach_object(value, "lease");
    JsonObject *source = ordered_detach_object(lease, "source");
    if (basis) json_object_remove_member(basis, "observedAtMs");
    if (stamp) json_object_remove_member(stamp, "epoch");
    if (permit) { json_object_remove_member(permit, "observedAtMs"); json_object_remove_member(permit, "expiresAtMs"); }
    if (plan) json_object_remove_member(plan, "id");
    if (expected) json_object_remove_member(expected, "observedAtMs");
    if (source) json_object_remove_member(source, "observedAtMs");
}

#endif
