@echo off
setlocal EnableExtensions

rem ===========================================================================
rem  SmartKB one-shot release script: Windows dev machine -> Ubuntu VM
rem  Mirrors the 5 steps of the republish manual in docs/ (ASCII-only on
rem  purpose: cmd.exe codepage issues would garble non-ASCII comments/output).
rem
rem  Usage:
rem    publish.bat          full release (build + package + deploy + verify)
rem    publish.bat api      same, but rebuild only the api container
rem    publish.bat web      same, but rebuild only the web container
rem
rem  Optional environment overrides:
rem    SMARTKB_HOST        ssh target          (default lee@192.168.245.128)
rem    SMARTKB_PROJECT     local repo root     (default E:\code\ai\CompanyAI)
rem    SMARTKB_PKG         local tarball path  (default E:\code\ai\SmartKB-deploy.tar.gz)
rem    DRYRUN=0            print the plan only, touch nothing
rem    SKIP_DB_BACKUP=1    skip the pre-deploy pg_dump
rem    NOPAUSE=1           do not wait for a key at the end
rem
rem  Safety: .env is backed up before the source dir is wiped and restored
rem  afterwards. The database volume is never touched; a pg_dump is taken
rem  because api applies EF migrations automatically on startup.
rem ===========================================================================

if not defined SMARTKB_HOST    set "SMARTKB_HOST=lee@192.168.245.128"
if not defined SMARTKB_PROJECT set "SMARTKB_PROJECT=E:\code\ai\CompanyAI"
if not defined SMARTKB_PKG     set "SMARTKB_PKG=E:\code\ai\SmartKB-deploy.tar.gz"

set "STAGE=%TEMP%\smartkb-deploy-stage"
set "SSHOPT=-o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10"
set "TARGET="
if /I "%~1"=="api" set "TARGET=api"
if /I "%~1"=="web" set "TARGET=web"

echo ============================================================
echo  SmartKB release
echo  host    : %SMARTKB_HOST%
echo  source  : %SMARTKB_PROJECT%
echo  package : %SMARTKB_PKG%
echo  rebuild : %TARGET% (blank = api + web)
echo ============================================================
echo.
echo  NOTE: api applies EF migrations automatically on startup.
echo        Pending migrations refresh sys_users.IsAdmin from roles:
echo        any account with the admin role becomes a full administrator.
echo        A pg_dump of the database is taken before this release.
echo.

if not defined DRYRUN goto :run
echo [DRYRUN] No build, upload, or deploy command was executed.
goto :done

:run
echo [1/6] Building solution locally ...
dotnet build "%SMARTKB_PROJECT%\SmartKB.slnx" --nologo -v q
if errorlevel 1 goto :fail

echo [2/6] Staging sources and creating tarball ...
if exist "%STAGE%" rd /s /q "%STAGE%"
if errorlevel 1 goto :fail
rem .npm-cache is a local npm download cache (can exceed 100 MB) and is not
rem part of the deployable source; .env / *.tar.gz must never be shipped.
robocopy "%SMARTKB_PROJECT%" "%STAGE%" /E /XD bin obj node_modules dist tests docs .git .workbuddy .vs .npm-cache /XF *.user *.bat *.tar.gz .env *.dump /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :fail
tar -czf "%SMARTKB_PKG%" -C "%STAGE%" .
if errorlevel 1 goto :fail

echo [3/6] Uploading tarball ...
scp %SSHOPT% "%SMARTKB_PKG%" "%SMARTKB_HOST%:~/SmartKB-deploy.tar.gz"
if errorlevel 1 goto :fail

echo [4/6] Backing up remote .env and database ...
ssh %SSHOPT% "%SMARTKB_HOST%" "test -f ~/smartkb/.env && cp ~/smartkb/.env ~/smartkb.env.bak && echo ENV_BACKUP_OK"
if errorlevel 1 goto :fail
if defined SKIP_DB_BACKUP goto :dbdone
ssh %SSHOPT% "%SMARTKB_HOST%" "docker exec smartkb-db pg_dump -U smartkb -d smartkb -Fc -f /tmp/smartkb-predeploy.dump && docker cp smartkb-db:/tmp/smartkb-predeploy.dump ~/smartkb-predeploy-$(date +%%Y%%m%%d_%%H%%M).dump && echo DB_BACKUP_OK"
if errorlevel 1 goto :fail
:dbdone

echo [5/6] Replacing remote sources and rebuilding containers ...
ssh %SSHOPT% "%SMARTKB_HOST%" "rm -rf ~/smartkb && mkdir -p ~/smartkb && tar -xzf ~/SmartKB-deploy.tar.gz -C ~/smartkb && cp ~/smartkb.env.bak ~/smartkb/.env && chmod 600 ~/smartkb/.env && rm ~/smartkb.env.bak && echo UNPACK_OK"
if errorlevel 1 goto :fail
if not defined TARGET goto :buildall
ssh %SSHOPT% "%SMARTKB_HOST%" "cd ~/smartkb && docker compose up -d --build %TARGET%"
if errorlevel 1 goto :fail
goto :built
:buildall
ssh %SSHOPT% "%SMARTKB_HOST%" "cd ~/smartkb && docker compose up -d --build"
if errorlevel 1 goto :fail
:built

echo [6/6] Verifying ...
rem /health is swallowed by the nginx SPA fallback, so also probe a real API
rem route: an unauthenticated /api/kbs must answer 401 when api is healthy.
ssh %SSHOPT% "%SMARTKB_HOST%" "cd ~/smartkb && docker compose ps && curl -s -o /dev/null -w 'health: %%{http_code}\n' http://localhost/health && curl -s -o /dev/null -w 'api: %%{http_code}\n' http://localhost/api/kbs"
if errorlevel 1 goto :fail

echo.
echo [OK] Release finished.
echo    - Open http://192.168.245.128/ and press Ctrl+Shift+R to force refresh.
echo    - DB backup on the VM: ~/smartkb-predeploy-*.dump
echo    - To inspect logs: ssh %SMARTKB_HOST%  then run: docker compose logs api --tail 40
goto :done

:fail
echo.
echo [FAILED] Release stopped at the step shown above.
echo   The remote .env backup is ~/smartkb.env.bak (once step 4 has run).
echo   Manual restore: cp ~/smartkb.env.bak ~/smartkb/.env ^&^& chmod 600 ~/smartkb/.env
echo   To inspect logs: ssh %SMARTKB_HOST%  then run: docker compose logs api --tail 40
if not defined NOPAUSE pause
exit /b 1

:done
echo.
if not defined NOPAUSE pause
exit /b 0
