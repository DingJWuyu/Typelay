#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/prctl.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* The host owns the supervisor; the supervisor owns a new session and its workers.
 * No shell is involved. Parent death and normal exit both reap descendants. */
static volatile sig_atomic_t stopping;
static void stop(int signal_number) { (void)signal_number; stopping = 1; }
static long milliseconds(void) {
    struct timespec time;
    if (clock_gettime(CLOCK_MONOTONIC, &time) != 0) _exit(125);
    return time.tv_sec * 1000 + time.tv_nsec / 1000000;
}
int main(int argc, char **argv) {
    if (argc < 2) return 64;
    pid_t parent = getppid();
    struct sigaction action = {.sa_handler = stop};
    sigemptyset(&action.sa_mask);
    sigaction(SIGTERM, &action, NULL);
    sigaction(SIGINT, &action, NULL);
    sigaction(SIGHUP, &action, NULL);
    if (prctl(PR_SET_CHILD_SUBREAPER, 1) || prctl(PR_SET_PDEATHSIG, SIGTERM)) return 125;
    if (getppid() != parent) return 125;
    pid_t supervisor = getpid();
    pid_t child = fork();
    if (child < 0) return 125;
    if (child == 0) {
        if (setsid() < 0 || prctl(PR_SET_PDEATHSIG, SIGKILL)) _exit(125);
        if (getppid() != supervisor) _exit(125);
        action.sa_handler = SIG_DFL;
        sigaction(SIGTERM, &action, NULL);
        sigaction(SIGINT, &action, NULL);
        sigaction(SIGHUP, &action, NULL);
        execvp(argv[1], argv + 1);
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
        if (reaped < 0 && errno == ECHILD) break;
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
