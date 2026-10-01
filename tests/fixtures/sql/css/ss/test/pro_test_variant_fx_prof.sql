use sbntest
go

-- sql-test --variant-profile source (headless-suite sql-test.variant.profile, --source-root tests/fixtures/sql).
-- Never deployed. Option sv099 is '-' in options.101 and '+' in options.101.GONZO, and no variant forces it,
-- so the branch compiled in shows which profile supplied the options layer.

if object_id('pro_test_variant_fx_prof') is not null
  drop proc pro_test_variant_fx_prof
go

-- The gate: reports which branch of sv099 was compiled in; adspl is the forced option.
create proc pro_test_variant_fx_prof
  @state varchar(8) output
as
begin
  select @state = 'unset'
  &if_adspl&
  &if_sv099&  select @state = 'on'  &endif_sv099&
  &ifn_sv099& select @state = 'off' &endifn_sv099&
  &endif_adspl&
end
go
