print 'batch one'
select 1, 'abc', convert(money,1234.5), convert(datetime,'2026-01-02 03:04:05'), null
go
create table #r (a int, b varchar(10))
go

insert #r values (1, 'x')
insert #r values (2, 'y')
select * from #r
go
selec 2
go
set nocount on
update #r set b = 'z'
select b from #r
go
exec sp_help 'zz_nonexistent'
go
select replicate('w', 100), 7, convert(float,1e20)
go
