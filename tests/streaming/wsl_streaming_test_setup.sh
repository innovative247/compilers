#!/bin/bash
SBX=/tmp/sql_case_test
mkdir -p "$SBX/CSS/ss/ba"
# Wipe + recopy all three so the sandbox is fresh
cp /mnt/c/_innovative/_source/current.sql/CSS/ss/ba/u_get_user_info_dummy.sql "$SBX/CSS/ss/ba/"
cp /mnt/c/_innovative/_source/current.sql/CSS/ss/ba/u_raiserror.sql "$SBX/CSS/ss/ba/"
cp /mnt/c/_innovative/_source/current.sql/CSS/ss/ba/pro_ba_gen_chg_log_dummy.sql "$SBX/CSS/ss/ba/"
ls "$SBX/CSS/ss/ba"
