- **Faster admin start-up and tests**: kalla-admin and kalla-api build their endpoints' request handling at compile time
  (ASP.NET's request delegate generator) instead of at each start, so a test host starts without compiling them again.
