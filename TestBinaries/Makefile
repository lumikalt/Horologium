CC      = riscv32-none-elf-gcc
CFLAGS  = -march=rv32i -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g
LDFLAGS = -T test.ld

.PHONY: all clean

all: test.elf

test.elf: crt0.s test.c test.ld
	$(CC) $(CFLAGS) $(LDFLAGS) crt0.s test.c -o $@

clean:
	rm -f test.elf
