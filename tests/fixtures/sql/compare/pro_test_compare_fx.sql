use sbntest
go

-- --compare-rev fixture for headless-suite sql-test.compare.*; the compare/ copy differs only in 'order by id desc'.

if object_id('pro_test_compare_fx') is not null
  drop proc pro_test_compare_fx
go

create proc pro_test_compare_fx
  @n int
as
begin
  print 'compare-fx %1!', @n
  select i = @n,
         s = 'line1' + char(10) + 'line2',
         c = convert(char(5), 'ab'),
         m = convert(money, 1.5),
         d = convert(datetime, '20260101 10:11:12.345'),
         b = 0x01AB,
         f = convert(bit, 1),
         z = convert(varchar(10), null)
  select id = 1, v = @n * 10
  union all select 2, @n * 20
  union all select 3, @n * 30
  order by id desc
  return 7
end
go

grant execute on pro_test_compare_fx to public
go
