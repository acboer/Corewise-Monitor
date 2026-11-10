@echo off
rem Atualiza o coletor Hardware Watch: de dois cliques neste arquivo (pede administrador).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0atualizar.ps1"
if errorlevel 1 pause
