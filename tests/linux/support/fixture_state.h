/* Live fixture observations need atomic visibility, not power-loss durability.
 * Never fsync these snapshots from the GTK thread: that can delay the actual
 * native input/overlay callbacks whose deadlines the fixture is observing. */
#ifndef TYPELAY_FIXTURE_STATE_H
#define TYPELAY_FIXTURE_STATE_H

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* A failed publication leaves the previous complete snapshot in place. The
 * caller must fail the fixture rather than silently reporting stale success. */
static int fixture_publish_state(const char *path, const char *contents) {
    char *temporary = malloc(strlen(path) + sizeof ".XXXXXX");
    if (!temporary) return ENOMEM;
    sprintf(temporary, "%s.XXXXXX", path);
    int descriptor = mkstemp(temporary), failure = 0;
    if (descriptor < 0) { failure = errno; goto done; }
    FILE *stream = fdopen(descriptor, "w");
    if (!stream) { failure = errno; close(descriptor); goto cleanup; }
    size_t length = strlen(contents);
    if (fwrite(contents, 1, length, stream) != length) failure = errno ? errno : EIO;
    if (fclose(stream) != 0 && !failure) failure = errno;
    if (!failure && rename(temporary, path) != 0) failure = errno;
cleanup:
    if (failure) unlink(temporary);
done:
    free(temporary);
    return failure;
}

#endif
