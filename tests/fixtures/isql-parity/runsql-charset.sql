select 'caf'+char(233)
go
select convert(varchar(100), replicate(char(233), 50)), 1
go
select x=char(201)+'tude', y=convert(char(4), char(233)+'a')
go
