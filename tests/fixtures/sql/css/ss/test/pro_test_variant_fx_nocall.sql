use sbntest
go

-- sql-test @variant chain-break source: never calls the gate, so a chain through it is refused.
-- Never deployed. Its text must not name the gate proc.

if object_id('pro_test_variant_fx_nocall') is not null
  drop proc pro_test_variant_fx_nocall
go

create proc pro_test_variant_fx_nocall
  @state varchar(8) output
as
begin
  select @state = 'nocall'
end
go
