/* slowread.so — put a real per-read latency on one directory, so a local NVMe can stand in for
 * the user's NAS (~12,8 ms per 16 KB read) without root, FUSE or a loop device.
 *
 * There is no /dev/fuse and no privileges in this container, so a throttled block device or a
 * FUSE filesystem is not available. LD_PRELOAD is: the shim intercepts pread/pread64/read/readv,
 * resolves the descriptor to its path through /proc/self/fd and sleeps proportionally to the
 * bytes actually returned, but only for paths under SLOWREAD_PREFIX. Everything else is passed
 * straight through with no added cost.
 *
 *   SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/   (default)
 *   SLOWREAD_MS_PER_16K=12.8                         (default)
 *
 * Build:  gcc -shared -fPIC -O2 -o slowread.so slowread.c -ldl
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static ssize_t (*real_pread64)(int, void *, size_t, off_t);
static ssize_t (*real_pread)(int, void *, size_t, off_t);
static ssize_t (*real_read)(int, void *, size_t);

static const char *prefix;
static double ns_per_16k;
static int ready;

#define CACHE 512
static struct {
    int fd;
    unsigned long ino;
    int slow;
} cache[CACHE];

static double ns_per_call;

static void init(void)
{
    if (ready)
        return;
    real_pread64 = dlsym(RTLD_NEXT, "pread64");
    real_pread = dlsym(RTLD_NEXT, "pread");
    real_read = dlsym(RTLD_NEXT, "read");
    const char *p = getenv("SLOWREAD_PREFIX");
    prefix = (p && *p) ? p : "/opt/data/jf12test/media-slow/";
    const char *m = getenv("SLOWREAD_MS_PER_16K");
    ns_per_16k = ((m && *m) ? atof(m) : 12.8) * 1e6;
    /* A share charges per round trip, not per byte: the same read costs ~13 ms whether it returns 4 KB or
       4 MB. SLOWREAD_MS_PER_CALL=<ms> reproduces that shape (fabji's Synology), which is the storage the
       extractor's read pattern has to be right for. */
    const char *c = getenv("SLOWREAD_MS_PER_CALL");
    ns_per_call = ((c && *c) ? atof(c) : 0.0) * 1e6;
    ready = 1;
}

/* Is this descriptor one of the throttled files? Resolved once per (fd, inode). */
static int is_slow(int fd)
{
    struct stat st;
    if (fstat(fd, &st) != 0)
        return 0;
    int slot = ((unsigned)fd) % CACHE;
    if (cache[slot].fd == fd && cache[slot].ino == (unsigned long)st.st_ino)
        return cache[slot].slow;

    char link[64], path[PATH_MAX];
    ssize_t n;
    snprintf(link, sizeof(link), "/proc/self/fd/%d", fd);
    n = readlink(link, path, sizeof(path) - 1);
    int slow = 0;
    if (n > 0) {
        path[n] = '\0';
        slow = strncmp(path, prefix, strlen(prefix)) == 0;
    }
    cache[slot].fd = fd;
    cache[slot].ino = (unsigned long)st.st_ino;
    cache[slot].slow = slow;
    return slow;
}

static void charge(ssize_t got)
{
    if (got <= 0)
        return;
    double ns = ns_per_call > 0.0 ? ns_per_call : ((double)got / 16384.0) * ns_per_16k;
    if (ns <= 0)
        return;
    struct timespec ts;
    ts.tv_sec = (time_t)(ns / 1e9);
    ts.tv_nsec = (long)(ns - (double)ts.tv_sec * 1e9);
    nanosleep(&ts, NULL);
}

ssize_t pread64(int fd, void *buf, size_t count, off_t offset)
{
    init();
    ssize_t r = real_pread64(fd, buf, count, offset);
    if (is_slow(fd))
        charge(r);
    return r;
}

ssize_t pread(int fd, void *buf, size_t count, off_t offset)
{
    init();
    ssize_t r = real_pread(fd, buf, count, offset);
    if (is_slow(fd))
        charge(r);
    return r;
}

ssize_t read(int fd, void *buf, size_t count)
{
    init();
    ssize_t r = real_read(fd, buf, count);
    if (is_slow(fd))
        charge(r);
    return r;
}
