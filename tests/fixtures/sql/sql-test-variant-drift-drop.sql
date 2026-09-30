-- Drops the proc the headless-suite drift case deploys, then prints how many remain (0 when clean).
if object_id('pro_test_variant_fx_drift') is not null drop proc pro_test_variant_fx_drift
go
declare @n int
select @n = count(*) from sysobjects where type = 'P' and name = 'pro_test_variant_fx_drift'
print 'drift-procs: %1!', @n
go
