-- sql-test fixture for the headless-suite drift and --variant-profile cases; deploy and run as a login that is dbo in sbntest.
-- Sources live under css/ss/test/ and drift/ and run with --source-root tests/fixtures/sql.
-- Each test calls its variant through a variable, so this file deploys while no variant exists.

-- Drift, same text: pro_test_variant_fx_drift is deployed from the file the index locates, so the check must pass.
if object_id('selftest_framework_variant_drift_same') is not null drop proc selftest_framework_variant_drift_same
go
create proc selftest_framework_variant_drift_same as
-- @variant: adspl=+ as dfs chain pro_test_variant_fx_drift
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_drift__dfs'
  exec @p @state = @state output
  if @state <> 'on'
  begin
    print 'FAIL: variant-drift-same got %1!', @state
    raiserror 50001 'variant-drift-same'
  end
end
go

-- Drift, differing text: the refusal must come before any compile, so the body never runs.
if object_id('selftest_framework_variant_drift_refuse') is not null drop proc selftest_framework_variant_drift_refuse
go
create proc selftest_framework_variant_drift_refuse as
-- @variant: adspl=+ as dfr chain pro_test_variant_fx_drift
-- @variant-source: pro_test_variant_fx_drift = drift/pro_test_variant_fx_drift.sql
begin
  print 'FAIL: variant-drift-refuse ran'
  raiserror 50001 'variant-drift-refuse'
end
go

-- Profile: sv099 is compiled in under GONZO and not under the runner's GONZO_TEST.
if object_id('selftest_framework_variant_prof_on') is not null drop proc selftest_framework_variant_prof_on
go
create proc selftest_framework_variant_prof_on as
-- @variant: adspl=+ as pfon chain pro_test_variant_fx_prof
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_prof__pfon'
  exec @p @state = @state output
  if @state <> 'on'
  begin
    print 'FAIL: variant-prof-on got %1!', @state
    raiserror 50001 'variant-prof-on'
  end
end
go

if object_id('selftest_framework_variant_prof_off') is not null drop proc selftest_framework_variant_prof_off
go
create proc selftest_framework_variant_prof_off as
-- @variant: adspl=+ as pfoff chain pro_test_variant_fx_prof
begin
  declare @p varchar(64), @state varchar(8)
  select @p = 'pro_test_variant_fx_prof__pfoff'
  exec @p @state = @state output
  if @state <> 'off'
  begin
    print 'FAIL: variant-prof-off got %1!', @state
    raiserror 50001 'variant-prof-off'
  end
end
go
