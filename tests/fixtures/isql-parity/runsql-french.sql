-- Français : « Élève à l'école, garçon, où ça ? »
create table #fr (v varchar(100) null)
insert #fr values ('àâäçéèêëîïôöùûüÿ ÀÂÄÇÉÈÊËÎÏÔÖÙÛÜ «» ÆæØøÅå')
select vb = convert(varbinary(100), v) from #fr
go
