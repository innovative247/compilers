#!/bin/bash
# Simulate a *post-rename* SVN checkout on ext4 by mirroring current.sql to /tmp
# with the same lowercase folder names that will exist after the user commits
# the 9 svn moves and other devs run `svn update`.
set -u
SBX=/tmp/sql_post_rename
rm -rf "$SBX"
mkdir -p "$SBX"
# Copy the real (already-renamed) current.sql tree
cp -r /mnt/c/_innovative/_source/current.sql/css "$SBX/"
cp -r /mnt/c/_innovative/_source/current.sql/ibs "$SBX/"
cp -r /mnt/c/_innovative/_source/current.sql/ios "$SBX/"

echo "=== sandbox layout (lowercase canonical) ==="
ls "$SBX"
echo
echo "=== options file at the post-rename path ==="
ls "$SBX/css/setup/options.101" 2>&1 | head -1
echo
echo "=== compiler invocation: GONZO_POST profile -> $SBX ==="
cd /mnt/c/_innovative/_source/compilers/bin/linux-x64
./isqlline 'select @@version' master GONZO_POST 2>&1 | tail -5
echo "exit: $?"
