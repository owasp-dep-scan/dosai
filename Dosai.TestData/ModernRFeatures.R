# Modern R source exercising the post-4.0 language baseline: the native pipe `|>` with the
# `_` placeholder (R 4.2+) in named arguments, and lambda-assigned functions using the `\(x)`
# shorthand (R 4.1+). The fallback line parser must recognize `name <- \(args) {` as a
# function declaration, attribute calls to the enclosing function, and ignore the comment
# prose (shapes like `word (R 4.1+)`) instead of reporting them as calls.

library(jsonlite)
library(dplyr)

render <- function(input) {
  cmd <- input$path |>
    file.path("bin") |>
    basename()
  system(cmd)
}

# Lambda-assigned function (R 4.1+): `process <- \(rows) { ... }`.
process <- \(rows) {
  rows |>
    lapply(\(row) row$value) |>
    sum()
}

# Piped named argument via `_` (R 4.3+).
plot_rows <- function(df) {
  df |>
    subset(select = c(x, y)) |>
    (\(data) head(data, n = 5))()
}
