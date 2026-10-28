-- it's a line comment with an apostrophe, em-dash — and arrow →
if object_id('jw_unconvertible_p') is not null drop proc jw_unconvertible_p
go
create proc jw_unconvertible_p as
/* nested /* — */ block comment ’ */
select 1
go
select c = convert(varbinary(255), text) from syscomments where id = object_id('jw_unconvertible_p')
drop proc jw_unconvertible_p
go
-- it's a comment before a literal
create table #t (u univarchar(20) null)
insert #t values ('a—b')
select u = convert(varbinary(20), u) from #t
go
