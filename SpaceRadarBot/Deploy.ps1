# Путь к проекту и папке publish
$localPublish = "C:\Programming\SpaceRadarBot\SpaceRadarBot\SpaceRadarBot\bin\Release\net10.0\linux-x64\publish"
$serverUser = "root"
$serverHost = "159.223.223.83"
$remotePath = "/root/bot"

# 1. Собираем проект
dotnet publish -c Release -o $localPublish

# 2. Копируем на сервер
scp -r "$localPublish\*" "${serverUser}@${serverHost}:${remotePath}/"

# 3. Перезапускаем сервис
ssh "${serverUser}@${serverHost}" "systemctl daemon-reload && systemctl restart telegrambot && systemctl status telegrambot --no-pager"