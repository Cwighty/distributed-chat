# Scaffold Nutrinova Entities

- Running this command will overwrite the NutrinovaDbContext and Entities folder
- Use partial classes to add additional functionality to the entities and context


```bash
dotnet ef dbcontext scaffold "Host=localhost;Port=5432;Database=chatdb;Username=admin;Password=chatpassword!" Npgsql.EntityFrameworkCore.PostgreSQL --project ./Chat.Data/ -c ChatDbContext --context-dir ./ -o Entities -f --no-onconfiguring
```
- Note: This is to be run from within the development container, the connection string is set up to work with the docker-compose networK


