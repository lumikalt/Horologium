// Real musl pthread_create/pthread_join composed with Horologium's clone()/futex() support
// (LinuxSyscallEmulator + MultiHartKernel's dormant hart slots). Confirmed by disassembling this
// exact binary (riscv64-unknown-linux-musl-gcc -static -pthread): pthread_create's only syscalls
// beyond clone() are SYS_mmap (the child's stack) and SYS_rt_sigprocmask (already a no-op ok in
// LinuxSyscallEmulator); pthread_join loops on SYS_futex(FUTEX_WAIT, &self->tid, ...) and
// __pthread_exit writes 0 to that same tid word and calls SYS_futex(FUTEX_WAKE, ...) itself in
// userspace before SYS_exit — musl never relies on the kernel's CLONE_CHILD_CLEARTID to do this,
// so no kernel-side ctid-clear-on-exit support is needed for a real pthread_join to complete.
#include <pthread.h>
#include <stdio.h>

static void *worker(void *arg) {
    long id = (long)arg;
    printf("thread %ld running\n", id);
    return NULL;
}

int main(void) {
    pthread_t t1, t2;
    pthread_create(&t1, NULL, worker, (void *)1);
    pthread_create(&t2, NULL, worker, (void *)2);
    pthread_join(t1, NULL);
    pthread_join(t2, NULL);
    printf("done\n");
    return 0;
}
