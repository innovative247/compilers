-- sql-test writer-mode fixture for headless-suite Test-SqlTest; sbntest tables only, writers restore `k > 0`.

if object_id('tbl_test_writer_fixture') is not null drop table tbl_test_writer_fixture
go
-- identity and timestamp: the restore needs identity_insert and must leave timestamp out.
create table tbl_test_writer_fixture (
  id  numeric(10,0) identity,
  k   int          not null,
  val varchar(30)  not null,
  ts  timestamp)
go
insert tbl_test_writer_fixture (k, val) values (1, 'one')
insert tbl_test_writer_fixture (k, val) values (2, 'two')
insert tbl_test_writer_fixture (k, val) values (3, 'three')
go

if object_id('tbl_test_writer_trig') is not null drop table tbl_test_writer_trig
go
create table tbl_test_writer_trig (
  k   int         not null,
  val varchar(30) not null)
go
insert tbl_test_writer_trig (k, val) values (1, 'one')
go
create trigger tri_test_writer_trig on tbl_test_writer_trig for insert as
begin
  declare @n int
  select @n = count(*) from inserted
end
go

-- Space-free for isqlline: the timestamp column is left out because a restore rewrites it.
if object_id('pro_test_writer_fixture_dump') is not null drop proc pro_test_writer_fixture_dump
go
create proc pro_test_writer_fixture_dump as
begin
  select id, k, val from tbl_test_writer_fixture order by id
  select k, val from tbl_test_writer_trig order by k
  -- The journal table is created by sql-test on first use, so it is read dynamically.
  if object_id('tbl_test_writer_journal') is null
    print 'writer-journal-rows: 0'
  else
    exec ('declare @n int select @n = count(*) from tbl_test_writer_journal where test like ''selftest[_]framework[_]writer[_]%'' print ''writer-journal-rows: %1!'', @n')
end
go

if object_id('selftest_framework_writer_pass') is not null drop proc selftest_framework_writer_pass
go
create proc selftest_framework_writer_pass as
-- @no-transaction
-- @restore: sbntest..tbl_test_writer_fixture where k > 0
begin
  update tbl_test_writer_fixture set val = 'changed' where k = 1
  delete tbl_test_writer_fixture where k = 2
  insert tbl_test_writer_fixture (k, val) values (4, 'four')
end
go

if object_id('selftest_framework_writer_fail') is not null drop proc selftest_framework_writer_fail
go
create proc selftest_framework_writer_fail as
-- @no-transaction
-- @restore: sbntest..tbl_test_writer_fixture where k > 0
begin
  update tbl_test_writer_fixture set val = 'changed' where k = 1
  insert tbl_test_writer_fixture (k, val) values (4, 'four')
  print 'FAIL: writer-fail'
  raiserror 50001 'writer-fail'
end
go

if object_id('selftest_framework_writer_skip') is not null drop proc selftest_framework_writer_skip
go
create proc selftest_framework_writer_skip as
-- @no-transaction
-- @restore: sbntest..tbl_test_writer_fixture where k > 0
begin
  update tbl_test_writer_fixture set val = 'changed' where k = 1
  delete tbl_test_writer_fixture where k = 3
  print 'SKIP: writer-skip'
  raiserror 50002 'writer-skip'
end
go

if object_id('selftest_framework_writer_error') is not null drop proc selftest_framework_writer_error
go
create proc selftest_framework_writer_error as
-- @no-transaction
-- @restore: sbntest..tbl_test_writer_fixture where k > 0
begin
  update tbl_test_writer_fixture set val = 'changed' where k = 2
  insert tbl_test_writer_fixture (k, val) values (5, 'five')
  raiserror 50003 'writer-error'
end
go

-- Run with --timeout 5: the rows are written before the wait.
if object_id('selftest_framework_writer_timeout') is not null drop proc selftest_framework_writer_timeout
go
create proc selftest_framework_writer_timeout as
-- @no-transaction
-- @restore: sbntest..tbl_test_writer_fixture where k > 0
begin
  update tbl_test_writer_fixture set val = 'changed' where k = 3
  insert tbl_test_writer_fixture (k, val) values (6, 'six')
  waitfor delay '00:00:30'
end
go

-- Refusals: each would write if run, so an unchanged dump proves nothing ran.
if object_id('selftest_framework_writer_refuse_notran') is not null drop proc selftest_framework_writer_refuse_notran
go
create proc selftest_framework_writer_refuse_notran as
-- @restore: sbntest..tbl_test_writer_fixture where k > 0
begin
  update tbl_test_writer_fixture set val = 'refused' where k = 1
end
go

if object_id('selftest_framework_writer_refuse_table') is not null drop proc selftest_framework_writer_refuse_table
go
create proc selftest_framework_writer_refuse_table as
-- @no-transaction
-- @restore: sbntest..tbl_test_writer_missing where k > 0
begin
  update tbl_test_writer_fixture set val = 'refused' where k = 1
end
go

if object_id('selftest_framework_writer_refuse_trigger') is not null drop proc selftest_framework_writer_refuse_trigger
go
create proc selftest_framework_writer_refuse_trigger as
-- @no-transaction
-- @restore: sbntest..tbl_test_writer_trig where k > 0
begin
  insert tbl_test_writer_trig (k, val) values (2, 'refused')
end
go

-- No-tran test without @restore: `select into` proves no transaction; the teardown removes its row.
if object_id('selftest_framework_notran_plain') is not null drop proc selftest_framework_notran_plain
go
create proc selftest_framework_notran_plain as
-- @no-transaction
begin
  select k into #t from tbl_test_writer_fixture
  insert tbl_test_writer_fixture (k, val) values (-1, 'notran')
end
go

if object_id('selftest_framework_notran_plain_teardown') is not null drop proc selftest_framework_notran_plain_teardown
go
create proc selftest_framework_notran_plain_teardown as
begin
  delete tbl_test_writer_fixture where k = -1
end
go
