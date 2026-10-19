#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* The host owns the supervisor; the supervisor owns a new session and its workers.
 * No shell is involved. Parent death and normal exit both reap descendants. */
static volatile sig_atomic_t stopping;
/* This is ownership evidence, never the worker's success status. */
enum { EXIT_PROOF = 200 };
static void stop(int signal_number) { (void)signal_number; stopping = 1; }
static long milliseconds(void) {
    struct timespec time;
    if (clock_gettime(CLOCK_MONOTONIC, &time) != 0) _exit(125);
    return time.tv_sec * 1000 + time.tv_nsec / 1000000;
}
int main(int argc, char **argv) {
    if (argc < 2) return 64;
    bool require_exit_proof = strcmp(argv[1], "--require-exit-proof") == 0;
    int command = require_exit_proof ? 2 : 1;
    if (argc <= command) return 64;
    pid_t parent = getppid();
    struct sigaction action = {.sa_handler = stop};
    sigemptyset(&action.sa_mask);
    sigaction(SIGTERM, &action, NULL);
    sigaction(SIGINT, &action, NULL);
    sigaction(SIGHUP, &action, NULL);
    if (prctl(PR_SET_CHILD_SUBREAPER, 1) || prctl(PR_SET_PDEATHSIG, SIGTERM)) return 125;
    if (getppid() != parent) return 125;
    if (require_exit_proof) {
        /* Do not fork until the owner releases the startup gate. Model hosts
         * retain a pidfd; the one-shot launcher owns this direct child.
         * Only this one byte belongs to us; subsequent stdin is worker protocol. */
        unsigned char ready;
        ssize_t count;
        do { count = read(STDIN_FILENO, &ready, 1); }
        while (count < 0 && errno == EINTR && !stopping);
        if (stopping || count != 1 || ready != 1) return EXIT_PROOF;
    }
    int model_use = -1;
    const char *model_lock = getenv("TYPELAY_MODEL_USE_LOCK");
    if (require_exit_proof && model_lock) {
        struct stat metadata;
        if (model_lock[0] != '/' ||
            (model_use = open(model_lock, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0600)) < 0 ||
            fstat(model_use, &metadata) || !S_ISREG(metadata.st_mode) || metadata.st_nlink != 1 ||
            metadata.st_uid != geteuid() || (metadata.st_mode & 077) ||
            flock(model_use, LOCK_SH | LOCK_NB)) {
            if (model_use >= 0) close(model_use);
            return EXIT_PROOF; /* No worker has been forked. */
        }
    }
    unsetenv("TYPELAY_MODEL_USE_LOCK");
    pid_t supervisor = getpid();
    pid_t child = fork();
    if (child < 0) return 125;
    if (child == 0) {
        if (model_use >= 0) close(model_use);
        if (setsid() < 0 || prctl(PR_SET_PDEATHSIG, SIGKILL)) _exit(125);
        if (getppid() != supervisor) _exit(125);
        action.sa_handler = SIG_DFL;
        sigaction(SIGTERM, &action, NULL);
        sigaction(SIGINT, &action, NULL);
        sigaction(SIGHUP, &action, NULL);
        execvp(argv[command], argv + command);
        /* stderr must not disclose argv, input, environment or exception detail. */
        fputs("TypeLay: child executable unavailable\n", stderr);
        _exit(127);
    }
    int status = 0, result = 125;
    bool child_done = false, terminating = false, killed = false;
    long deadline = 0;
    for (;;) {
        pid_t reaped;
        while ((reaped = waitpid(-1, &status, WNOHANG)) > 0) {
            if (reaped == child) {
                child_done = true;
                result = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
            }
        }
        if (reaped < 0 && errno == ECHILD) {
            if (!require_exit_proof) break;
            /* Reaping the root alone is insufficient. A subreaped descendant
             * outside the group also prevents ECHILD, so it cannot grant proof. */
            if (kill(-child, 0) < 0 && errno == ESRCH) return EXIT_PROOF;
        } else if (reaped < 0 && errno != EINTR) {
            return 125;
        }
        if ((stopping || child_done) && !terminating) {
            terminating = true;
            deadline = milliseconds() + 700;
            kill(-child, SIGTERM);
            kill(child, SIGTERM); /* also covers fork-to-setsid startup */
        }
        if (terminating && !killed && milliseconds() >= deadline) {
            killed = true;
            kill(-child, SIGKILL);
            kill(child, SIGKILL);
        }
        struct timespec delay = {.tv_nsec = 10000000};
        nanosleep(&delay, NULL);
    }
    return stopping ? 128 + SIGTERM : result;
}
