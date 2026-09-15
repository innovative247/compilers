#!/bin/bash
SBX=/tmp/sql_case_test
rm -rf "$SBX"
mkdir -p "$SBX/CSS/ss/ba"
cp /mnt/c/_innovative/_source/current.sql/CSS/ss/ba/u_get_user_info_dummy.sql "$SBX/CSS/ss/ba/"
cp /mnt/c/_innovative/_source/current.sql/CSS/ss/ba/u_raiserror.sql "$SBX/CSS/ss/ba/"
cp /mnt/c/_innovative/_source/current.sql/CSS/ss/ba/pro_ba_gen_chg_log_dummy.sql "$SBX/CSS/ss/ba/"
echo "=== created files ==="
ls "$SBX/CSS/ss/ba"
echo
echo "=== case-sensitivity test (lowercase css/ should NOT find anything; uppercase CSS/ SHOULD) ==="
ls "$SBX/css/ss/ba/u_raiserror.sql" 2>&1
ls "$SBX/CSS/ss/ba/u_raiserror.sql" 2>&1
