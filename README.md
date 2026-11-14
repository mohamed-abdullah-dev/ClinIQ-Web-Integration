# ClinIQ Web Integration

ASP.NET Core API for integrating the Doctor and Admin web screens. The solution includes the shared workflows those screens depend on, the SQL Server migrations, and the existing tests.

## Run locally

Install the .NET SDK version in `global.json` and SQL Server Express. The default Development configuration uses Windows authentication against `.\SQLEXPRESS` and the separate database `ClinIQWebIntegrationDb`. If your SQL instance differs, override `ConnectionStrings__DefaultConnection` locally and use a dedicated integration database. The commands below use PowerShell from the repository root.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet tool restore
dotnet build ClinIQ.sln -c Release
dotnet ef database update --project src/ClinIQ.Infrastructure --startup-project src/ClinIQ.API --configuration Release --no-build
dotnet run --project src/ClinIQ.API -c Release --no-build --launch-profile http -- --seed-specialties
dotnet run --project src/ClinIQ.API -c Release --no-build --launch-profile http -- --bootstrap-admin
dotnet run --project src/ClinIQ.API -c Release --no-build --launch-profile http
```

The API listens at `http://localhost:5084` and accepts the local web origin `http://localhost:5173`. The API exposes `/openapi/v1.json` in Development. [openapi.json](./openapi.json) is an export for the frontend team to import or use for mocks.

## Test accounts and first walkthrough

1. Create the first Admin with the interactive bootstrap command above. Use synthetic accounts and documents for integration testing.
2. Find the generated confirmation email under `.artifacts/auth-private/development-emails/`. The Development mail adapter writes local files. The frontend confirmation screen can read `userId` and `token` from the link and send them as JSON to `POST /api/auth/confirm-email`. The same process applies to the Doctor.
3. Load `GET /api/specialties` and submit the Doctor form to `POST /api/auth/doctors/register` as `multipart/form-data`. Use the OpenAPI schema for field names, evidence, and enum values.
4. Sign in as Admin. Load `GET /api/admin/doctor-registrations`, open a request, and review its documents. Approve or reject using the returned `version` and a `reason`.
5. Confirm the Doctor's email and sign in as that Doctor after approval. Load `GET /api/auth/me` and `GET /api/doctors/me/professional-profile`.

## Web session

Set `credentials: 'include'` in fetch or `withCredentials: true` in Axios. Fetch `GET /api/auth/web/csrf` before login and use its `requestToken` as the `X-CSRF-TOKEN` header.

```http
POST /api/auth/web/login
Content-Type: application/json
X-CSRF-TOKEN: <requestToken>

{"email":"<test-email>","password":"<test-password>","rememberMe":false}
```

Fetch a fresh CSRF token after login and refresh. Use it on writes, refresh, and logout. Access and refresh credentials are in HttpOnly cookies. Keep the web app and API on the same site for the existing Strict cookie policy. For a shared deployment, use HTTPS and configure the exact web origin if the API is on a separate origin.

Use the ProblemDetails `title` and validation `errors` to handle errors. For example `403 Auth.DoctorPendingReview` means the Doctor still needs approval. For a version conflict, reload the resource before retrying the edit.

## Screen to API map

| Screen or workflow | API |
| --- | --- |
| Sign in, current user, refresh, logout | `/api/auth/web/*`, `GET /api/auth/me` |
| Doctor signup, email confirmation, resubmission | `POST /api/auth/doctors/register`, `/api/auth/confirm-email`, `/api/auth/doctors/resubmission` |
| Doctor profile and professional changes | `/api/doctors/me/professional-profile`, `/api/doctors/me/professional-changes` |
| Doctor practice locations, fee and evidence | `/api/doctors/me/practice-locations`, `/api/premises-corrections` |
| Doctor schedule, exceptions and sessions | `/api/doctors/me/scheduling` |
| Doctor bookings, queue and attendance | `/api/attendance/doctors` |
| Invitations, staff and linking a practice | `/api/work-invitations`, `/api/workforce`, `/api/practice-links` |
| Departure, closure and emergency requests | `/api/operations` |
| Doctor plan and Pro trial | `/api/doctors/me/subscription` |
| Admin registration and professional change review | `/api/admin/doctor-registrations`, `/api/admin/doctor-professional-changes` |
| Admin specialties | `/api/admin/specialties` |
| Admin clinic, practice and location correction review | `/api/admin/clinic-applications`, `/api/admin/practice-locations`, `/api/admin/premises-corrections` |
| Admin attendance disputes and emergency decisions | `/api/admin/attendance-feedback`, `/api/admin/operational-emergencies` |
| Admin review moderation and notice handling | `/api/admin/visit-reviews`, `/api/admin/feedback-deliveries`, `/api/admin/booking-notices` |

OpenAPI gives the individual paths, request bodies, and response models. Some operations require a current resource version and scope-specific permission in addition to a signed-in role.

## Fast checks

```powershell
dotnet test ClinIQ.sln -c Release --no-build --filter 'Category!=SqlServerIntegration'
dotnet run --project tests/ClinIQ.Phase0.Checks -c Release --no-build --no-launch-profile
```
