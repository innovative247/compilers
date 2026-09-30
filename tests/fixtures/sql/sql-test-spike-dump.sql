-- Reports what a headless-suite sql-test.spike.* run may leave behind: scratch procs, journal rows, items.
-- The journal tables are created by sql-test on first use, so they are read dynamically.
declare @n int
select @n = count(*) from sysobjects where type = 'P' and name like '%[_][_]spike'
print 'spike-procs: %1!', @n
go
if object_id('tbl_test_writer_journal') is null
  print 'spike-journal-rows: 0'
else
  exec ('declare @n int
    select @n = count(*) from tbl_test_writer_journal where test like ''spike pro[_]test[_]spike[_]fx%''
    print ''spike-journal-rows: %1!'', @n')
go
if object_id('tbl_test_writer_item') is null
  print 'spike-items: 0'
else
  exec ('declare @n int
    select @n = count(*) from tbl_test_writer_item i, tbl_test_writer_journal j
     where i.journal_id = j.journal_id and j.test like ''spike pro[_]test[_]spike[_]fx%''
    print ''spike-items: %1!'', @n')
go
