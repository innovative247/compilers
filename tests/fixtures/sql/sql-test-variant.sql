-- sql-test @variant fixture for headless-suite Test-SqlTest; deploy and run as a login that is dbo in sbntest.
-- The chain sources live under css/ss/test/ and run with --source-root tests/fixtures/sql.
-- Each test calls the entry variant through a variable, so this file deploys while no variant exists.

-- Space-free for isqlline. Every count must be 0 between cases.
if object_id('pro_test_variant_dump') is not null drop proc pro_test_variant_dump
go
create proc pro_test_variant_dump as
begin
  declare @n int
  select @n = count(*) from sysobjects where type = 'P' and name like 'pro[_]test[_]variant[_]fx%[_][_]%'
  print 'variant-procs: %1!', @n
  select name from sysobjects where type = 'P' and name like 'pro[_]test[_]variant[_]fx%[_][_]%' order by name
  -- The journal tables are created by sql-test on first use, so they are read dynamically.
  if object_id('tbl_test_writer_journal') is null
  begin
    print 'variant-journal-rows: 0'
    print 'variant-journal-live: 0'
  end
  else
    exec ('declare @n int, @l int
      select @n = count(*) from tbl_test_writer_journal where test like ''selftest[_]framework[_]variant[_]%''
      select @l = count(*) from tbl_test_writer_journal j
        where j.test like ''selftest[_]framework[_]variant[_]%'' and exists (select 1 from master..sysprocesses p
          where (p.spid = j.spid and p.kpid = j.kpid) or (p.spid = j.test_spid and p.kpid = j.test_kpid))
      print ''variant-journal-rows: %1!'', @n
      print ''variant-journal-live: %1!'', @l')
  if object_id('tbl_test_writer_item') is null
    print 'variant-items: 0'
  else
    exec ('declare @n int
      select @n = count(*) from tbl_test_writer_item where kind = ''V'' and obj like ''pro[_]test[_]variant[_]fx%''
      print ''variant-items: %1!'', @n')
end
go

if object_id('selftest_framework_variant_on') is not null drop proc selftest_framework_variant_on
go
create proc selftest_framework_variant_on as
-- @variant: adspl=+ as on chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_entry__on'
  exec @p @state = @state output
  if @state <> 'on'
  begin
    print 'FAIL: variant-on got %1!', @state
    raiserror 50001 'variant-on'
  end
end
go

if object_id('selftest_framework_variant_off') is not null drop proc selftest_framework_variant_off
go
create proc selftest_framework_variant_off as
-- @variant: adspl=- as off chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_entry__off'
  exec @p @state = @state output
  if @state <> 'off'
  begin
    print 'FAIL: variant-off got %1!', @state
    raiserror 50001 'variant-off'
  end
end
go

if object_id('selftest_framework_variant_drop_pass') is not null drop proc selftest_framework_variant_drop_pass
go
create proc selftest_framework_variant_drop_pass as
-- @variant: adspl=+ as on chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_entry__on'
  exec @p @state = @state output
  if @state <> 'on'
  begin
    print 'FAIL: variant-drop-pass got %1!', @state
    raiserror 50001 'variant-drop-pass'
  end
end
go

-- Fails after the variant ran: the drop must still happen.
if object_id('selftest_framework_variant_drop_fail') is not null drop proc selftest_framework_variant_drop_fail
go
create proc selftest_framework_variant_drop_fail as
-- @variant: adspl=+ as on chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_entry__on'
  exec @p @state = @state output
  print 'FAIL: variant-drop-fail after %1!', @state
  raiserror 50001 'variant-drop-fail'
end
go

-- Refusals: each body would FAIL if it ran, so an ERROR with the reason proves nothing ran.
if object_id('selftest_framework_variant_refuse_unknown') is not null drop proc selftest_framework_variant_refuse_unknown
go
create proc selftest_framework_variant_refuse_unknown as
-- @variant: zzvnope=+ as unk chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  print 'FAIL: variant-refuse-unknown ran'
  raiserror 50001 'variant-refuse-unknown'
end
go

if object_id('selftest_framework_variant_refuse_chain') is not null drop proc selftest_framework_variant_refuse_chain
go
create proc selftest_framework_variant_refuse_chain as
-- @variant: adspl=+ as brk chain pro_test_variant_fx_nocall > pro_test_variant_fx
begin
  print 'FAIL: variant-refuse-chain ran'
  raiserror 50001 'variant-refuse-chain'
end
go

-- Same tag, different option: refused only when both are discovered in one run.
if object_id('selftest_framework_variant_conflict_a') is not null drop proc selftest_framework_variant_conflict_a
go
create proc selftest_framework_variant_conflict_a as
-- @variant: adspl=+ as cfl chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_entry__cfl'
  exec @p @state = @state output
  if @state <> 'on'
  begin
    print 'FAIL: variant-conflict-a got %1!', @state
    raiserror 50001 'variant-conflict-a'
  end
end
go

if object_id('selftest_framework_variant_conflict_b') is not null drop proc selftest_framework_variant_conflict_b
go
create proc selftest_framework_variant_conflict_b as
-- @variant: adspl=- as cfl chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_entry__cfl'
  exec @p @state = @state output
  if @state <> 'off'
  begin
    print 'FAIL: variant-conflict-b got %1!', @state
    raiserror 50001 'variant-conflict-b'
  end
end
go

-- Only the sweep case runs it, then kills its runner mid-wait; the sweep must drop its variants.
if object_id('selftest_framework_variant_victim') is not null drop proc selftest_framework_variant_victim
go
create proc selftest_framework_variant_victim as
-- @variant: adspl=+ as vic chain pro_test_variant_fx_entry > pro_test_variant_fx
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_entry__vic'
  exec @p @state = @state output
  waitfor delay '00:01:30'
end
go
