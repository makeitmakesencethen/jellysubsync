/* slowread.so — put a real per-read latency on one directory, so a local NVMe can stand in for
 * the user's NAS without root, FUSE or a loop device.
 *
 * There is no /dev/fuse and no privileges in this container, so a throttled block device or a
 * FUSE filesystem is not available. LD_PRELOAD is: the shim intercepts pread/pread64/read/readv,
 * resolves the descriptor to its path through /proc/self/fd and sleeps before returning, but only
 * for paths under SLOWREAD_PREFIX. Everything else is passed straight through with no added cost.
 *
 *   SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/   (default)
 *   SLOWREAD_MS_PER_16K=12.8                         (default when neither is given)
 *   SLOWREAD_MS_PER_CALL=10                          (a share that charges per round trip)
 *   SLOWREAD_FIRST_CALL_MS=231                       (cold: the first read(s) cost this instead)
 *   SLOWREAD_FIRST_CALLS=1                           (how many reads are cold, default 1)
 *   SLOWREAD_FD_FIRST_MS=231                         (the first read on a freshly opened handle is cold)
 *   SLOWREAD_FD_FIRST_READS=1                        (how many of them, default 1)
 *   SLOWREAD_COLD_GAP_MS=2000                        (a read after this much quiet is cold too, and this
 *                                                    one survives the library scan that reads the volume
 *                                                    minutes before the probe does)
 *
 * BOTH halves are charged when both are set: the round trip and the bytes it carried. Charging
 * only one is what made earlier benchmarks wrong in both directions - the per-call term alone says
 * a 325 MB pass costs a few round trips, the per-byte term alone says the thousands of small reads
 * cost nothing. fabji's share is 10 ms per read AND ~11 MB/s (1,46 ms per 16 KB), so a pass that
 * reads 325 MB there spends ~30 s on the bytes however its reads are arranged.
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
    int charged;   /* reads charged on this handle, for the cold-first-read knob */
} cache[CACHE];

static double ns_per_call;
static int charge_bytes;
static int charge_calls;

/* A volume that is cold, or busy with something else, answers the *first* read of a process far slower
   than its steady state - the S41 defect is a single such read becoming the volume's class. These two
   knobs reproduce that shape: the first SLOWREAD_FIRST_CALLS charged reads cost SLOWREAD_FIRST_CALL_MS
   each, and everything after them costs the normal per-call/per-byte price. */
static double ns_first_call;
static int first_calls;
static int charged_reads;

/* The same shape, triggered by time instead of by a count: a read that follows SLOWREAD_COLD_GAP_MS of
   no reads costs SLOWREAD_COLD_GAP_MS's price. This is the field defect exactly - the share answered one
   read in 231 ms while it was busy or cold and 13-46 ms once it was at rest - and unlike a count it
   survives the library scan, which reads the same volume minutes before the probe does. */
static double ns_cold_gap;
static struct timespec last_charged;

/* The knob that reproduces S41 without depending on ordering: the first SLOWREAD_FD_FIRST_READS reads on
   a *newly opened descriptor* cost SLOWREAD_FD_FIRST_MS, whatever their size. The probe opens its own
   handle and reads once, so it gets the cold price; the reads that follow in the same pass, on the same
   or another already-used handle, get the steady one. A share that answers an open-then-read in 231 ms
   and a warm read in 13 ms is exactly what the field showed. */
static double ns_fd_first;
static int fd_first_reads;

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
    const char *f = getenv("SLOWREAD_FIRST_CALL_MS");
    ns_first_call = ((f && *f) ? atof(f) : 0.0) * 1e6;
    const char *n = getenv("SLOWREAD_FIRST_CALLS");
    first_calls = (n && *n) ? atoi(n) : 1;
    const char *g = getenv("SLOWREAD_COLD_GAP_MS");
    ns_cold_gap = ((g && *g) ? atof(g) : 0.0) * 1e6;
    const char *d = getenv("SLOWREAD_FD_FIRST_MS");
    ns_fd_first = ((d && *d) ? atof(d) : 0.0) * 1e6;
    const char *k = getenv("SLOWREAD_FD_FIRST_READS");
    fd_first_reads = (k && *k) ? atoi(k) : 1;
    charge_bytes = m && *m;
    charge_calls = c && *c;
    if (!charge_bytes && !charge_calls)
        charge_bytes = 1; /* neither given: the plain per-16 KB profile */
    ready = 1;
}

/* Is this descriptor one of the throttled files? Resolved once per (fd, inode). */
static int slot_of(int fd, int *slot_out)
{
    struct stat st;
    if (fstat(fd, &st) != 0)
        return 0;
    int slot = ((unsigned)fd) % CACHE;
    if (cache[slot].fd == fd && cache[slot].ino == (unsigned long)st.st_ino) {
        *slot_out = slot;
        return 1;
    }

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
    cache[slot].charged = 0;   /* a new handle: its first reads are the cold ones */
    *slot_out = slot;
    return 1;
}

static int is_slow(int fd)
{
    int slot;
    if (!slot_of(fd, &slot))
        return 0;
    return cache[slot].slow;
}

static void charge(ssize_t got, int on_this_fd);

static void charge_fd(int fd, ssize_t got)
{
    int slot = -1;
    slot_of(fd, &slot);
    int on_this_fd = slot >= 0 ? ++cache[slot].charged : 1;
    charge(got, on_this_fd);
}

static void charge(ssize_t got, int on_this_fd)
{
    if (got <= 0)
        return;
    charged_reads++;
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    double since_last = (last_charged.tv_sec || last_charged.tv_nsec)
        ? (now.tv_sec - last_charged.tv_sec) * 1e9 + (now.tv_nsec - last_charged.tv_nsec)
        : 0.0;
    int idle = ns_cold_gap > 0.0 && since_last >= ns_cold_gap;
    double ns = 0.0;
    if (ns_fd_first > 0.0 && on_this_fd <= fd_first_reads) {
        ns = ns_fd_first; /* cold: the first read(s) on a handle that was just opened */
    } else if (ns_first_call > 0.0 && (charged_reads <= first_calls || idle)) {
        ns = ns_first_call; /* cold: the read that follows a quiet spell, whatever its size */
    } else {
        if (charge_calls)
            ns += ns_per_call;
        if (charge_bytes)
            ns += ((double)got / 16384.0) * ns_per_16k;
    }
    if (ns <= 0)
        return;
    struct timespec ts;
    ts.tv_sec = (time_t)(ns / 1e9);
    ts.tv_nsec = (long)(ns - (double)ts.tv_sec * 1e9);
    nanosleep(&ts, NULL);
    clock_gettime(CLOCK_MONOTONIC, &last_charged);
}

ssize_t pread64(int fd, void *buf, size_t count, off_t offset)
{
    init();
    ssize_t r = real_pread64(fd, buf, count, offset);
    if (is_slow(fd))
        charge_fd(fd, r);
    return r;
}

ssize_t pread(int fd, void *buf, size_t count, off_t offset)
{
    init();
    ssize_t r = real_pread(fd, buf, count, offset);
    if (is_slow(fd))
        charge_fd(fd, r);
    return r;
}

ssize_t read(int fd, void *buf, size_t count)
{
    init();
    ssize_t r = real_read(fd, buf, count);
    if (is_slow(fd))
        charge_fd(fd, r);
    return r;
}
