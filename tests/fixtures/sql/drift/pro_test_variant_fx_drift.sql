use sbntest
go

-- sql-test drift-check source that differs from the deployed proc (headless-suite sql-test.variant.refuse.drift).
-- Named by -- @variant-source in selftest_framework_variant_drift_refuse; never deployed, and outside css/ss,
-- so the source index never sees it. The only difference is the 'unset' literal.

if object_id('pro_test_variant_fx_drift') is not null
  drop proc pro_test_variant_fx_drift
go

-- The gate: reports which branch of the adspl option is compiled into.
create proc pro_test_variant_fx_drift
  @state varchar(8) output
as
begin
  select @state = 'none'
  &if_adspl&  select @state = 'on'  &endif_adspl&
  &ifn_adspl& select @state = 'off' &endifn_adspl&
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
  select @state = @state /* pad 012345678901234567890123456789012345 */
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
end
go
