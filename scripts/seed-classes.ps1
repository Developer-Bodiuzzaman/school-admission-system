$classes = @(
    @{ name = "Class 6"; totalSeats = 100 },
    @{ name = "Class 7"; totalSeats = 100 },
    @{ name = "Class 8"; totalSeats = 100 },
    @{ name = "Class 9"; totalSeats = 100 },
    @{ name = "Class 10"; totalSeats = 100 }
)

foreach ($class in $classes) {
    $body = $class | ConvertTo-Json
    Invoke-RestMethod `
        -Uri "http://localhost:8080/classes" `
        -Method Post `
        -ContentType "application/json" `
        -Body $body | Out-Null
}

Invoke-RestMethod -Uri "http://localhost:8080/classes" -Method Get
