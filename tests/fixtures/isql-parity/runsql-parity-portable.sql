print 'batch one'
select 1, convert(money,1234.5), convert(datetime,'2026-01-02 03:04:05'), convert(numeric(10,3),1.5), null
go
create table #r (a int, b int null)
go

insert #r values (1, 10)
insert #r values (2, null)
select * from #r
go
select a from #r where 1=2
go
set nocount on
update #r set b = 0
select b from #r
go
