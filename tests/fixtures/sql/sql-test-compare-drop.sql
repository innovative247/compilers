-- Drops the proc the headless-suite compare cases deploy, then prints how many pro_test_compare_fx% objects remain (0 when clean).
if object_id('pro_test_compare_fx') is not null drop proc pro_test_compare_fx
go
declare @n int
select @n = count(*) from sysobjects where name like 'pro[_]test[_]compare[_]fx%'
print 'compare-objects: %1!', @n
go
