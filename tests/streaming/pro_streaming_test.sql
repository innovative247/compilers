use sbnmaster
go

if exists (select 1 from sysobjects where name='pro_streaming_test' and type='P')
  drop procedure pro_streaming_test
go

create procedure pro_streaming_test
as
declare @i int, @big varchar(2000)
select @i = 1
-- Each row is ~1500 chars so it forces its own TDS packet (default 512 bytes)
-- and the server has to flush per iteration rather than buffering 6 tiny rows
-- into a single packet. This tests whether the streaming bottleneck is packet-fill
-- threshold (server-side) or AseClient's reader pump.
select @big = replicate('x', 1500)
while @i <= 5
begin
  select 'streaming tick ' + convert(varchar(3), @i) as msg, getdate() as tm, @big as filler
  waitfor delay '0:00:01'
  select @i = @i + 1
end
select 'streaming done' as msg, getdate() as tm, @big as filler
return 0
go

grant execute on pro_streaming_test to public
go
