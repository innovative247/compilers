use sbntest
go

-- sql-test @variant chain source (headless-suite Test-SqlTest, --source-root tests/fixtures/sql).
-- Never deployed: sql-test compiles <proc>__<tag> copies from it. Caller and callee share this
-- file on purpose, so each member's compile must select only its own batches.

if object_id('pro_test_variant_fx') is not null
  drop proc pro_test_variant_fx
go

-- The gate: reports which branch of the adspl option was compiled in.
create proc pro_test_variant_fx
  @state varchar(8) output
as
begin
  select @state = 'unset'
  &if_adspl&  select @state = 'on'  &endif_adspl&
  &ifn_adspl& select @state = 'off' &endifn_adspl&
end
go

if object_id('pro_test_variant_fx_entry') is not null
  drop proc pro_test_variant_fx_entry
go

-- The chain entry the tests call; it reaches the gate only through this exec.
create proc pro_test_variant_fx_entry
  @state varchar(8) output
as
begin
  exec pro_test_variant_fx @state = @state output
end
go
