# Turns the development realm into the hosted one.
.clients |= map(select(.clientId != "kb-dev-cli"))
| (.clients[] | select(.clientId == "kb-web")) |= (
    .redirectUris = ["/*"]
  | .webOrigins = ["+"]
  | .attributes["post.logout.redirect.uris"] = "/*")
| .users |= map(.credentials[0].value = "__PW_" + (.username | ascii_upcase) + "__")
