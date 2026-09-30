-- Reports what a headless-suite sql-test.compare.* run may leave behind: scratch procs, journal rows, 'V' items.
-- The journal tables are created by sql-test on first use, so they are read dynamically.
declare @n int
select @n = count(*) from sysobjects where type = 'P' and name like 'pro[_]test[_]compare[_]fx[_][_]%'
print 'compare-scratch: %1!', @n
go
if object_id('tbl_test_writer_journal') is null
  print 'compare-journal-rows: 0'
else
  exec ('declare @n int
    select @n = count(*) from tbl_test_writer_journal where test like ''compare-rev pro[_]test[_]compare[_]fx%''
    print ''compare-journal-rows: %1!'', @n')
go
if object_id('tbl_test_writer_item') is null
  print 'compare-items: 0'
else
  exec ('declare @n int
    select @n = count(*) from tbl_test_writer_item where kind = ''V'' and obj like ''pro[_]test[_]compare[_]fx%''
    print ''compare-items: %1!'', @n')
go
