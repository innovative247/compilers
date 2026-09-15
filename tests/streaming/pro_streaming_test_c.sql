use sbnmaster
go

if exists (select 1 from sysobjects where name='pro_streaming_test_c' and type='P')
  drop procedure pro_streaming_test_c
go

create procedure pro_streaming_test_c
as
select 'streaming_c finite query — single result-set' as msg, getdate() as tm
return 0
go

grant execute on pro_streaming_test_c to public
go
