/* shortread.so - make a storage answer with fewer bytes than it was asked for.
 *
 * FIX_PLAN's B25: the Matroska reader reads a subtitle block's payload and, when the storage returns fewer
 * bytes than the block declares, keeps what it got (`if (payloadRead < payloadLength) payload =
 * payload.AsSpan(0, payloadRead)`) and emits it as the cue's text. A share that answers with a short read
 * - a truncated NFS response, a share that drops the tail of a large read under load - therefore produces
 * silently truncated subtitles instead of a refusal, which is the same shape of defect as D17's cross-track
 * cue offsets and E2's duplicate cues: the pass reports success and the output is wrong.
 *
 * A healthy local file cannot produce a short read, so this shim does: it intercepts the position-based
 * reads the reader uses (RandomAccess.Read -> pread/pread64) for files under SHORTREAD_PREFIX and returns
 * half of what was asked on every Nth call. The bytes it does return are the real ones, so the file stays
 * valid and only the read is short - exactly what the storage does when it fails this way.
 *
 *   build:  gcc -shared -fPIC -O2 -o shortread.so shortread.c -ldl
 *   use:    LD_PRELOAD=./shortread.so SHORTREAD_PREFIX=/path/to/fixtures/ SHORTREAD_EVERY=4 ./your-program
 *
 * SHORTREAD_EVERY defaults to 4 (every fourth read is short); SHORTREAD_MODE=half (default) returns half the
 * requested bytes, SHORTREAD_MODE=minus1 returns one byte less, which is the smallest possible short read
 * and the hardest case for a reader to notice.
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <unistd.h>

static ssize_t (*real_pread)(int, void *, size_t, off_t);
static ssize_t (*real_pread64)(int, void *, size_t, off_t);
static long every = 0;
static int mode_minus_one = 0;
static const char *prefix = NULL;
static int matched[4096];
static __thread long counter = 0;
static int debug = 0;

static void init(void)
{
    if (!real_pread) {
        real_pread = dlsym(RTLD_NEXT, "pread");
    }
    if (!real_pread64) {
        real_pread64 = dlsym(RTLD_NEXT, "pread64");
    }
    if (!every) {
        const char *e = getenv("SHORTREAD_EVERY");
        every = e ? atol(e) : 4;
        if (every < 1) {
            every = 1;
        }
        prefix = getenv("SHORTREAD_PREFIX");
        debug = getenv("SHORTREAD_DEBUG") != NULL;
        const char *m = getenv("SHORTREAD_MODE");
        mode_minus_one = m && strcmp(m, "minus1") == 0;
    }
}

/* Is this descriptor one of the files the caller asked us to shorten? Cached per fd. */
static int is_target(int fd)
{
    if (fd < 0 || fd >= (int)(sizeof(matched) / sizeof(matched[0]))) {
        return 0;
    }
    if (matched[fd] != 0) {
        return matched[fd] > 0;
    }
    if (!prefix || !*prefix) {
        matched[fd] = -1;
        return 0;
    }
    char link[PATH_MAX];
    char path[64];
    snprintf(path, sizeof(path), "/proc/self/fd/%d", fd);
    ssize_t n = readlink(path, link, sizeof(link) - 1);
    if (n <= 0) {
        matched[fd] = -1;
        return 0;
    }
    link[n] = '\0';
    int hit = strncmp(link, prefix, strlen(prefix)) == 0;
    if (debug && !hit) {
        fprintf(stderr, "[shortread] fd %d is %s (outside %s)\n", fd, link, prefix);
    }
    matched[fd] = hit ? 1 : -1;
    return hit;
}

static size_t shorten(int fd, size_t count)
{
    if (count <= 1 || !is_target(fd)) {
        return count;
    }
    if (++counter % every != 0) {
        return count;
    }
    if (debug) {
        fprintf(stderr, "[shortread] shortening a %zu byte read to fd %d\n", count, fd);
    }
    if (mode_minus_one) {
        return count - 1;
    }
    size_t half = count / 2;
    return half > 0 ? half : 1;
}

ssize_t pread(int fd, void *buf, size_t count, off_t offset)
{
    init();
    if (!real_pread) {
        errno = ENOSYS;
        return -1;
    }
    size_t want = shorten(fd, count);
    return real_pread(fd, buf, want, offset);
}

ssize_t pread64(int fd, void *buf, size_t count, off_t offset)
{
    init();
    if (!real_pread64) {
        return pread(fd, buf, count, offset);
    }
    size_t want = shorten(fd, count);
    ssize_t got = real_pread64(fd, buf, want, offset);
    return got;
}
