root=0
for path in $(grep -lzFx "VEX_WSL_SESSION=$1" /proc/[0-9]*/environ 2>/dev/null); do
    pid=${path#/proc/}; pid=${pid%/environ}
    if [ "$root" -eq 0 ] || [ "$pid" -lt "$root" ]; then root=$pid; fi
done
[ "$root" -gt 0 ] || exit 1
printf '%s\n' "$root"
ps -eo pid=,ppid=,pgid=,tpgid=,comm=,args= -ww
