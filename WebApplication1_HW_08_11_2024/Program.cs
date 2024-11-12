
using Azure;
using Microsoft.Data.SqlClient;
using System.Reflection;
using System.Text;

//На основе рассмотренного примера с пользователями, реализовать следующие возможности:
//1) Добавление пользователя.                +
//2) Удаления пользователя.                +
//3) Редактирование пользователя.                -
//4) Поиск пользователей по имени.                -
//5) Сортировка пользователей на основе выпадающего списка (по имени или возрасту).                -
//6) (Необязательный пункт, но можно, если было мало) Реализовать пагинацию. 
//Внизу таблицы отображать кнопки, с помощью которых можно выполнять навигацию по пользователям,
//за раз выводить по 10 человек на страницу.


var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var configurationService = app.Services.GetService<IConfiguration>();
string connectionString = configurationService["ConnectionStrings:DefaultConnection"];
app.UseStaticFiles();

app.Run(async (context) =>
{
    var response = context.Response;
    var request = context.Request;
    const int PageSize = 2;
    response.ContentType = "text/html; charset=utf-8";

    if (request.Path == "/")
    {
        int pageNumber = int.TryParse(request.Query["page"], out var page) ? page : 1;
        List<User> users = new List<User>();

        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            string query = @"
            SELECT Id, Name, Age, PhotoPath 
            FROM Users 
            ORDER BY Id 
            OFFSET @Offset ROWS 
            FETCH NEXT @PageSize ROWS ONLY";

            SqlCommand command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@Offset", (pageNumber - 1) * PageSize);
            command.Parameters.AddWithValue("@PageSize", PageSize);

            using (SqlDataReader reader = await command.ExecuteReaderAsync())
            {
                if (reader.HasRows)
                {
                    while (await reader.ReadAsync())
                    {
                        users.Add(new User(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
                    }
                }
            }
        }
        int totalUserCount = 0;
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            SqlCommand countCommand = new SqlCommand("SELECT COUNT(*) FROM Users", connection);
            totalUserCount = (int)await countCommand.ExecuteScalarAsync();
        }

        int totalPages = (int)Math.Ceiling(totalUserCount / (double)PageSize);

        await response.WriteAsync(GenerateHtmlPage(GenerateUserCards(users) + GeneratePagination(pageNumber, totalPages), "All Users from DataBase"));
    }
    else if (request.Method == "GET" && request.Path == "/users")
    {
        try
        {
            int currentPage = 1;
            if (request.Query.ContainsKey("page") && int.TryParse(request.Query["page"], out int parsedPage))
            {
                currentPage = parsedPage > 0 ? parsedPage : 1;
            }

            List<User> users = new List<User>();
            int totalUsersCount = 0;

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                string countQuery = "SELECT COUNT(*) FROM Users";
                SqlCommand countCommand = new SqlCommand(countQuery, connection);
                totalUsersCount = (int)await countCommand.ExecuteScalarAsync();

                string query = "SELECT * FROM Users ORDER BY Id OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@Offset", (currentPage - 1) * PageSize);
                command.Parameters.AddWithValue("@PageSize", PageSize);

                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        users.Add(new User(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
                    }
                }
            }

            int totalPages = (int)Math.Ceiling((double)totalUsersCount / PageSize);

            string userCardsHtml = GenerateUserCards(users);
            string paginationHtml = GeneratePagination(currentPage, totalPages);

            await response.WriteAsync(GenerateHtmlPage(userCardsHtml + paginationHtml, "Users from DataBase"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            response.StatusCode = 500;
        }
    }
    else if (request.Path == "/addUserPage")
    {
        await response.WriteAsync(GenerateAddUserPage());
    }
    else if (request.Path == "/addUser" && request.Method == "POST")
    {
        var form = await request.ReadFormAsync();
        string name = form["name"];
        int age = int.Parse(form["age"]);
        var file = form.Files["photo"];

        if (file != null && file.Length > 0)
        {
            var uploads = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot/uploads");
            Directory.CreateDirectory(uploads);
            var fileExtension = Path.GetExtension(file.FileName);
            var uniqueFileName = $"{Guid.NewGuid()}{fileExtension}";
            var filePath = Path.Combine(uploads, uniqueFileName);
            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                string query = "INSERT INTO Users (Name, Age, PhotoPath) VALUES (@Name, @Age, @PhotoPath)";
                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@Name", name);
                command.Parameters.AddWithValue("@Age", age);
                command.Parameters.AddWithValue("@PhotoPath", $"/uploads/{uniqueFileName}");
                await command.ExecuteNonQueryAsync();
            }
            context.Response.Redirect("/");
        }
        else
        {
            await context.Response.WriteAsync("File upload failed.");
        }
    }
    else if (request.Path.StartsWithSegments("/deleteUser", out var matchedPath2) &&
             int.TryParse(matchedPath2.Value.Trim('/'), out int userId2))
    {
        string photoPath = "";
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            SqlCommand selectCommand = new SqlCommand("SELECT PhotoPath FROM Users WHERE Id = @id", connection);
            selectCommand.Parameters.AddWithValue("@id", userId2);
            var result = await selectCommand.ExecuteScalarAsync();
            if (result != null)
            {
                photoPath = result.ToString();
            }
            SqlCommand deleteCommand = new SqlCommand("DELETE FROM Users WHERE Id = @id", connection);
            deleteCommand.Parameters.AddWithValue("@id", userId2);
            await deleteCommand.ExecuteNonQueryAsync();
        }
        if (!string.IsNullOrEmpty(photoPath))
        {
            string uploadFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            string photoFullPath = Path.Combine(uploadFolderPath, Path.GetFileName(photoPath));

            if (File.Exists(photoFullPath))
            {
                File.Delete(photoFullPath);
            }
        }
        response.Redirect("/");
    }
    else if (request.Method == "POST" && request.Path == "/sortUsers")
    {
        try
        {
            var form = await request.ReadFormAsync();
            string sortBy = form["sortBy"];
            int currentPage = 1;

            if (request.Query.ContainsKey("page") && int.TryParse(request.Query["page"], out int parsedPage))
            {
                currentPage = parsedPage > 0 ? parsedPage : 1;
            }

            List<User> sortedUsers = new List<User>();
            int totalUsersCount = 0;

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                string countQuery = "SELECT COUNT(*) FROM Users";
                SqlCommand countCommand = new SqlCommand(countQuery, connection);
                totalUsersCount = (int)await countCommand.ExecuteScalarAsync();

                string query = sortBy == "name"
                    ? "SELECT * FROM Users ORDER BY Name OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY"
                    : "SELECT * FROM Users ORDER BY Age OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@Offset", (currentPage - 1) * PageSize);
                command.Parameters.AddWithValue("@PageSize", PageSize);

                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        sortedUsers.Add(new User(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
                    }
                }
            }

            int totalPages = (int)Math.Ceiling((double)totalUsersCount / PageSize);

            string userCardsHtml = GenerateUserCards(sortedUsers);
            string paginationHtml = GeneratePagination(currentPage, totalPages, sortBy);

            string htmlPage = GenerateHtmlPage(userCardsHtml + paginationHtml, "Users from DataBase (sorted)");
            await response.WriteAsync(htmlPage);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            response.StatusCode = 500;
        }
    }
    else if (request.Method == "GET" && request.Path == "/sortUsers")
    {
        try
        {
            string sortBy = request.Query.ContainsKey("sortBy") ? request.Query["sortBy"] : "name";
            int currentPage = 1;

            if (request.Query.ContainsKey("page") && int.TryParse(request.Query["page"], out int parsedPage))
            {
                currentPage = parsedPage > 0 ? parsedPage : 1;
            }

            List<User> sortedUsers = new List<User>();
            int totalUsersCount = 0;

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                string countQuery = "SELECT COUNT(*) FROM Users";
                SqlCommand countCommand = new SqlCommand(countQuery, connection);
                totalUsersCount = (int)await countCommand.ExecuteScalarAsync();

                string query = sortBy == "name"
                    ? "SELECT * FROM Users ORDER BY Name OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY"
                    : "SELECT * FROM Users ORDER BY Age OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@Offset", (currentPage - 1) * PageSize);
                command.Parameters.AddWithValue("@PageSize", PageSize);

                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        sortedUsers.Add(new User(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
                    }
                }
            }

            int totalPages = (int)Math.Ceiling((double)totalUsersCount / PageSize);

            string userCardsHtml = GenerateUserCards(sortedUsers);
            string paginationHtml = GeneratePagination(currentPage, totalPages, sortBy);

            await response.WriteAsync(GenerateHtmlPage(userCardsHtml + paginationHtml, "Users from DataBase (sorted)"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            response.StatusCode = 500;
        }
    }
    else if (request.Method == "GET" && request.Path == "/searchUsers")
    {
        try
        {
            string searchName = request.Query.ContainsKey("name") ? request.Query["name"] : "";
            int currentPage = 1;

            if (request.Query.ContainsKey("page") && int.TryParse(request.Query["page"], out int parsedPage))
            {
                currentPage = parsedPage > 0 ? parsedPage : 1;
            }

            List<User> filteredUsers = new List<User>();
            int totalUsersCount = 0;

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                string countQuery = "SELECT COUNT(*) FROM Users WHERE Name LIKE @Name";
                SqlCommand countCommand = new SqlCommand(countQuery, connection);
                countCommand.Parameters.AddWithValue("@Name", "%" + searchName + "%");
                totalUsersCount = (int)await countCommand.ExecuteScalarAsync();

                string query = "SELECT * FROM Users WHERE Name LIKE @Name ORDER BY Id OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@Name", "%" + searchName + "%");
                command.Parameters.AddWithValue("@Offset", (currentPage - 1) * PageSize);
                command.Parameters.AddWithValue("@PageSize", PageSize);

                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        filteredUsers.Add(new User(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
                    }
                }
            }

            int totalPages = (int)Math.Ceiling((double)totalUsersCount / PageSize);

            string userCardsHtml = GenerateUserCards(filteredUsers);
            string paginationHtml = GenerateSearchPagination(currentPage, totalPages, searchName);

            await response.WriteAsync(GenerateHtmlPage(userCardsHtml + paginationHtml, $"Search Results for '{searchName}'"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            response.StatusCode = 500;
        }
    }
    else if (request.Path.StartsWithSegments("/editUser", out var matchedPath) &&
    int.TryParse(matchedPath.Value.Trim('/'), out int userId))
    {
        if (request.Method == "GET")
        {
            User user = null;
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                SqlCommand command = new SqlCommand("SELECT Id, Name, Age, PhotoPath FROM Users WHERE Id = @id", connection);
                command.Parameters.AddWithValue("@id", userId);
                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        user = new User(
                            reader.GetInt32(0),
                            reader.GetString(1),
                            reader.GetInt32(2),
                            reader.GetString(3)
                        );
                    }
                }
            }

            if (user != null)
            {
                await response.WriteAsync(GenerateEditUserPage(user));
            }
            else
            {
                response.StatusCode = 404;
                await response.WriteAsync("User Not Found");
            }
        }
        else if (request.Method == "POST")
        {
            try
            {
                var form = await request.ReadFormAsync();
                string name = form["name"];
                int age = int.Parse(form["age"]);
                var file = form.Files["photo"];
                string oldPhotoPath = "";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    SqlCommand selectCommand = new SqlCommand("SELECT PhotoPath FROM Users WHERE Id = @id", connection);
                    selectCommand.Parameters.AddWithValue("@id", userId);
                    var result = await selectCommand.ExecuteScalarAsync();
                    oldPhotoPath = result as string ?? "";
                }

                string newPhotoPath = oldPhotoPath;

                if (file != null && file.Length > 0)
                {
                    string uploadFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");

                    if (!Directory.Exists(uploadFolderPath))
                    {
                        Directory.CreateDirectory(uploadFolderPath);
                    }

                    string oldPhotoFullPath = Path.Combine(uploadFolderPath, Path.GetFileName(oldPhotoPath));

                    if (File.Exists(oldPhotoFullPath))
                    {
                        File.Delete(oldPhotoFullPath);
                    }

                    string newFileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                    newPhotoPath = Path.Combine("uploads", newFileName);
                    string newPhotoFullPath = Path.Combine(uploadFolderPath, newFileName);

                    using (var fileStream = new FileStream(newPhotoFullPath, FileMode.Create))
                    {
                        await file.CopyToAsync(fileStream);
                    }
                }

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    SqlCommand updateCommand = new SqlCommand(
                        "UPDATE Users SET Name = @name, Age = @age, PhotoPath = @photoPath WHERE Id = @id", connection);
                    updateCommand.Parameters.AddWithValue("@name", name);
                    updateCommand.Parameters.AddWithValue("@age", age);
                    updateCommand.Parameters.AddWithValue("@photoPath", newPhotoPath);
                    updateCommand.Parameters.AddWithValue("@id", userId);

                    int affectedRows = await updateCommand.ExecuteNonQueryAsync();
                    if (affectedRows == 0)
                    {
                        Console.WriteLine("Ошибка: не удалось обновить запись пользователя.");
                    }
                }

                response.Redirect("/");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка обновления пользователя: {ex.Message}");
                response.StatusCode = 500;
            }
        }

    }
    else
    {
        response.StatusCode = 404;
        await response.WriteAsync("Page Not Found");
    }
});
app.Run();

//static string GenerateUserCards(List<User> users)
//{
//    StringBuilder cardsHtml = new StringBuilder();
//    cardsHtml.Append("<div class=\"row\">");

//    foreach (var user in users)
//    {
//        cardsHtml.Append($"""
//            <div class="col-md-4">
//                <div class="card mb-4 shadow-sm">
//                    <img src="{user.PhotoPath}" class="card-img-top" alt="User Photo" style="height: 200px; object-fit: cover;">
//                    <div class="card-body">
//                        <h5 class="card-title">{user.Name}</h5>
//                        <p class="card-text">Age: {user.Age}</p>
//                        <div class="d-flex justify-content-between align-items-center">
//                            <a href="/editUser/{user.Id}" class="btn btn-warning btn-sm">Edit</a>
//                            <a href="/deleteUser/{user.Id}" class="btn btn-danger btn-sm">Delete</a>
//                        </div>
//                    </div>
//                </div>
//            </div>
//        """);
//    }

//    cardsHtml.Append("</div>");
//    return cardsHtml.ToString();
//}
//static string GenerateSearchPagination(int currentPage, int totalPages, string searchName)
//{
//    StringBuilder paginationHtml = new StringBuilder();
//    paginationHtml.Append("<nav aria-label=\"Page navigation\"><ul class=\"pagination justify-content-center\">");

//    if (currentPage > 1)
//    {
//        paginationHtml.Append($"""
//            <li class="page-item">
//                <a class="page-link" href="/searchUsers?name={Uri.EscapeDataString(searchName)}&page={currentPage - 1}" aria-label="Previous">
//                    <span aria-hidden="true">&laquo;</span>
//                </a>
//            </li>
//        """);
//    }
//    for (int i = 1; i <= totalPages; i++)
//    {
//        if (i == currentPage)
//        {
//            paginationHtml.Append($"""
//                <li class="page-item active">
//                    <a class="page-link" href="/searchUsers?name={Uri.EscapeDataString(searchName)}&page={i}">{i}</a>
//                </li>
//            """);
//        }
//        else
//        {
//            paginationHtml.Append($"""
//                <li class="page-item">
//                    <a class="page-link" href="/searchUsers?name={Uri.EscapeDataString(searchName)}&page={i}">{i}</a>
//                </li>
//            """);
//        }
//    }
//    if (currentPage < totalPages)
//    {
//        paginationHtml.Append($"""
//            <li class="page-item">
//                <a class="page-link" href="/searchUsers?name={Uri.EscapeDataString(searchName)}&page={currentPage + 1}" aria-label="Next">
//                    <span aria-hidden="true">&raquo;</span>
//                </a>
//            </li>
//        """);
//    }

//    paginationHtml.Append("</ul></nav>");
//    return paginationHtml.ToString();
//}

//static string GeneratePagination(int currentPage, int totalPages, string sortBy = null)
//{
//    StringBuilder paginationHtml = new StringBuilder();
//    paginationHtml.Append("<nav aria-label=\"Page navigation\"><ul class=\"pagination justify-content-center\">");

//    if (currentPage > 1)
//    {
//        string prevPageUrl = sortBy != null
//            ? $"/sortUsers?page={currentPage - 1}&sortBy={sortBy}"
//            : $"/users?page={currentPage - 1}";

//        paginationHtml.Append($"""
//            <li class="page-item">
//                <a class="page-link" href="{prevPageUrl}" aria-label="Previous">
//                    <span aria-hidden="true">&laquo;</span>
//                </a>
//            </li>
//        """);
//    }

//    for (int i = 1; i <= totalPages; i++)
//    {
//        string pageUrl = sortBy != null
//            ? $"/sortUsers?page={i}&sortBy={sortBy}"
//            : $"/users?page={i}";

//        if (i == currentPage)
//        {
//            paginationHtml.Append($"""
//                <li class="page-item active">
//                    <a class="page-link" href="{pageUrl}">{i}</a>
//                </li>
//            """);
//        }
//        else
//        {
//            paginationHtml.Append($"""
//                <li class="page-item">
//                    <a class="page-link" href="{pageUrl}">{i}</a>
//                </li>
//            """);
//        }
//    }

//    if (currentPage < totalPages)
//    {
//        string nextPageUrl = sortBy != null
//            ? $"/sortUsers?page={currentPage + 1}&sortBy={sortBy}"
//            : $"/users?page={currentPage + 1}";

//        paginationHtml.Append($"""
//            <li class="page-item">
//                <a class="page-link" href="{nextPageUrl}" aria-label="Next">
//                    <span aria-hidden="true">&raquo;</span>
//                </a>
//            </li>
//        """);
//    }

//    paginationHtml.Append("</ul></nav>");
//    return paginationHtml.ToString();
//}
//static string GenerateHtmlPage(string body, string header)
//{
//    string html = $"""
//        <!DOCTYPE html>
//        <html>
//        <head>
//            <meta charset="utf-8" />
//            <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/css/bootstrap.min.css" rel="stylesheet" 
//            integrity="sha384-KK94CHFLLe+nY2dmCWGMq91rCGa5gtU4mk92HdvYe+M/SXH301p5ILy+dN9+nJOZ" crossorigin="anonymous">
//            <title>{header}</title>
//        </head>
//        <body>
//        <div class="container">
//            <h2 class="d-flex justify-content-center">{header}</h2>
//            <div class="mt-5 mb-4 d-flex justify-content-between align-items-center">
//                <!-- Строка поиска -->
//                <form action="/searchUsers" method="GET" class="d-flex">
//                    <input type="text" name="name" class="form-control" placeholder="Search by Name" />
//                    <button type="submit" class="btn btn-secondary ms-2">Search</button>
//                </form>
//                <form action="/sortUsers" method="POST" class="d-flex ms-3">
//                    <select name="sortBy" class="form-select">
//                        <option value="name">Sort by Name</option>
//                        <option value="age">Sort by Age</option>
//                    </select>
//                    <button type="submit" class="btn btn-primary ms-2">Sort</button>
//                </form>
//                <a href="/addUserPage" class="btn btn-success ms-3">Add User</a>
//            </div>
//            {body}
//            <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/js/bootstrap.bundle.min.js" 
//            integrity="sha384-ENjdO4Dr2bkBIFxQpeoTz1HIcje39Wm4jDKdf19U8gI4ddQ3GYNS7NTKfAdVQSZe" crossorigin="anonymous"></script>
//        </div>
//        </body>
//        </html>
//        """;
//    return html;
//}
//static string GenerateAddUserPage()
//{
//    return $"""
//        <!DOCTYPE html>
//        <html>
//        <head>
//            <meta charset="utf-8" />
//            <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/css/bootstrap.min.css" rel="stylesheet">
//            <title>Add User</title>
//        </head>
//        <body>
//        <div class="container mt-3">
//            <h3>Add New User</h3>
//            <form method="post" action="/addUser" enctype="multipart/form-data">
//                <div class="mb-3">
//                    <label for="name" class="form-label">Name:</label>
//                    <input type="text" id="name" name="name" class="form-control" required>
//                </div>
//                <div class="mb-3">
//                    <label for="age" class="form-label">Age:</label>
//                    <input type="number" id="age" name="age" class="form-control" min="1" required>
//                </div>
//                <div class="mb-3">
//                    <label for="photo" class="form-label">Photo:</label>
//                    <input type="file" id="photo" name="photo" class="form-control" accept="image/*" required>
//                </div>
//                <button type="submit" class="btn btn-success">Submit</button>
//                <a href="/" class="btn btn-secondary">Cancel</a>
//            </form>
//        </div>
//        </body>
//        </html>
//    """;
//}
//static string GenerateEditUserPage(User user)
//{
//    return $"""
//        <!DOCTYPE html>
//        <html>
//        <head>
//            <meta charset="utf-8" />
//            <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/css/bootstrap.min.css" rel="stylesheet">
//            <title>Edit User</title>
//        </head>
//        <body>
//        <div class="container mt-3">
//            <h3>Edit User</h3>
//            <form method="post" action="/editUser/{user.Id}" enctype="multipart/form-data">
//                <div class="mb-3">
//                    <label for="name" class="form-label">Name:</label>
//                    <input type="text" id="name" name="name" class="form-control" value="{user.Name}" required>
//                </div>
//                <div class="mb-3">
//                    <label for="age" class="form-label">Age:</label>
//                    <input type="number" id="age" name="age" class="form-control" min="1" value="{user.Age}" required>
//                </div>
//                <div class="mb-3">
//                    <label for="photo" class="form-label">Update Photo:</label>
//                    <input type="file" id="photo" name="photo" class="form-control">
//                    <p>Current Photo: <img src="" style="width: 100px; height: auto;"></p>
//                </div>
//                <button type="submit" class="btn btn-success">Save Changes</button>
//                <a href="/" class="btn btn-secondary">Cancel</a>
//            </form>
//        </div>
//        </body>
//        </html>
//    """;
//}
record User(int Id, string Name, int Age, string PhotoPath)
{
    public User(string Name, int Age, string PhotoPath) : this(0, Name, Age, PhotoPath) { }
}
