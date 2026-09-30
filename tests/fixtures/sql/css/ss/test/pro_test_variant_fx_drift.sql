use sbntest
go

-- sql-test drift-check source (headless-suite sql-test.variant.refuse.drift, --source-root tests/fixtures/sql).
-- The case deploys this file as dbo and drops the proc afterwards. The text is longer than 255 characters,
-- and its padding places a blank at the end of syscomments row 1 and at the start of row 3.

if object_id('pro_test_variant_fx_drift') is not null
  drop proc pro_test_variant_fx_drift
go

-- The gate: reports which branch of the adspl option is compiled into.
create proc pro_test_variant_fx_drift
  @state varchar(8) output
as
begin
  select @state = 'unset'
  &if_adspl&  select @state = 'on'  &endif_adspl&
  &ifn_adspl& select @state = 'off' &endifn_adspl&
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
  select @state = @state /* pad 012345678901234567890123456789012345 */
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
  select @state = @state /* pad 0123456789012345678901234567890123456789012345678901234567890123456789 */
end
go
