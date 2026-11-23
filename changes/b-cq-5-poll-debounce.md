- **Nothing loads after a page is left, and a failed job check says so** (#960): the audit log's filters, the test
  questions' search and the question dialog's document search no longer fetch once their page or dialog is closed
  while a change waits. A job whose status can't be read ends the wait as failed instead of showing it as running:
  *Run evaluation* says the evaluation failed. A run or publication page whose refresh fails stops refreshing and
  shows the error.
