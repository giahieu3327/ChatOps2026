@echo off
:: Thiet lap tieu de cho cua so chinh de phan biet
title CHATOPS_MAIN_STRESS

echo ========================================================
echo KICH HOAT 30 LUONG CURL SONG SONG DE STRESS TEST...
echo THOI GIAN COOLDOWN: CHO 15-30 GIAY DE DOCKER CAP NHAT CPU
echo --------------------------------------------------------
echo [CHU Y] DE DUNG TEST: AN CTRL + C HOAC TAT CUA SO NAY.
echo ========================================================
echo.

:: Vong lap khoi chay 30 luong con chay ngam voi tieu de rieng biet
for /L %%x in (1,1,30) do (
   start "CHATOPS_SUB_CURL" /B cmd /c "for /L %%i in (1,0,2) do @curl -s -o NUL http://myshopsqlpro.chatopsnet.cloud-ip.cc:880/api/products"
)

:: Giu cua so chinh luon song de cho nguoi dung an Ctrl+C
:LOOP
pause > nul
goto LOOP

:: Phan doan xu ly khi nguoi dung an Ctrl+C hoac dong cua so
:interrupt
echo.
echo --------------------------------------------------------
echo DANG QUET SACH CAC LUONG CURL NGAM... XIN CHO GIAY LAT...
:: Giet toan bo cac tien trinh cmd con co tieu de CHATOPS_SUB_CURL
taskkill /FI "WINDOWTITLE eq CHATOPS_SUB_CURL*" /F > nul 2>&1
echo KET THUC STRESS TEST THANH CONG!
exit