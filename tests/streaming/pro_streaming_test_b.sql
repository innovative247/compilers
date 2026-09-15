use sbnmaster
go

if exists (select 1 from sysobjects where name='pro_streaming_test_b' and type='P')
  drop procedure pro_streaming_test_b
go

create procedure pro_streaming_test_b
as
declare @i int, @big varchar(2000)
select @i = 1, @big = replicate('y', 1500)
while @i <= 3
begin
  select 'streaming_b row ' + convert(varchar(3), @i) as msg, getdate() as tm, @big as filler
  waitfor delay '0:00:01'
  select @i = @i + 1
end
return 0
go

grant execute on pro_streaming_test_b to public
go
