use sbnmaster
go

if exists (select 1 from sysobjects where name='pro_streaming_forever' and type='P')
  drop procedure pro_streaming_forever
go

-- Never-ending streaming test proc. Mirrors the ma_algen pattern: an infinite
-- WHILE 1=1 loop that emits one result-set per iteration, ~1 second apart.
--
-- Each row carries a 1500-byte filler so the row exceeds the default TDS
-- packet size (~512 bytes) and the server is forced to flush per iteration
-- instead of buffering small rows into one packet. (Same reason
-- pro_streaming_test uses 1500-byte rows — see the comment there.)
--
-- HOW TO STOP IT:
--   - From the same terminal: Ctrl-C in isqlline / runsql
--   - From a different shell: connect to the SAME server and:
--         isqlline "kill <SPID>" master GONZO
--     (find your SPID with `iwho GONZO` — your row has cmd 'pro_streaming_forever')
--   - The proc has no commit/transaction, so killing leaves no DB state behind.
--
-- HOW TO VERIFY STREAMING IS WORKING:
--   - Each row's `tm` column shows the server-side time the row was generated.
--   - Watch the cadence in the terminal — rows should appear ~1 second apart.
--   - If they appear in bursts after long delays, streaming is BROKEN.
--   - The compilers must NOT be at v2.0.69 or earlier (that's the version
--     range where SequentialAccess wasn't passed; the network reader buffered
--     until proc completion — which never happens here, so you'd see nothing).

create procedure pro_streaming_forever
as
declare @i int, @big varchar(2000)
select @i = 0, @big = replicate('x', 1500)
while 1 = 1
begin
  select @i = @i + 1
  select 'tick ' + convert(varchar(8), @i) as msg, getdate() as tm, @big as filler
  waitfor delay '0:00:01'
end
return 0
go

grant execute on pro_streaming_forever to public
go
