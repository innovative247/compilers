use sbntest
go

-- sql-test --spike fixture (headless-suite sql-test.spike.*). Never deployed by the suite beyond
-- its own scratch copy; the suite measures line ranges of this file, so keep line numbers stable.

if object_id('pro_test_spike_fx') is not null
  drop proc pro_test_spike_fx
go

create proc pro_test_spike_fx @n int
as
begin
  declare @i int, @c int
  select @c = count(*) from sysobjects
  select @i = 0
  while @i < @n
  begin
    select @c = count(*) from sysusers
    select @i = @i + 1
  end
  select @c = count(*) from syscolumns
  select @c = count(*),
         @i = max(id)
    from sysindexes
   where id > 0
  if @n = 0
    select @c = 0
end
go

grant execute on pro_test_spike_fx to public
go
