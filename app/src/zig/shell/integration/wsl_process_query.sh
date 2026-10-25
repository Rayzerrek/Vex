root=0
for path in $(grep -lzFx "VEX_WSL_SESSION=$1" /proc/[0-9]*/environ 2>/dev/null); do
    pid=${path#/proc/}; pid=${pid%/environ}
    if [ "$root" -eq 0 ] || [ "$pid" -lt "$root" ]; then root=$pid; fi
done
[ "$root" -gt 0 ] || exit 1
printf '%s\n' "$root"
# Hex framing keeps whitespace and newlines in Linux names out of the row protocol.
printf 'cwd '
readlink -en "/proc/$root/cwd" 2>/dev/null | od -An -v -tx1 | tr -d ' \n'
printf '\n'
ps -eo pid=,ppid=,pgid=,tpgid=,comm=,args= -ww
