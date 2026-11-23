- **A stored stage output missing a required value fails again** (#998, after the .docx fix above): only a value that
  may be empty (a Word document's page count, a section's heading or pages) reads as empty when it is left out. A
  missing route or format fails the job as before, instead of reading as a default (a missing route would have sent a
  document down the HTML route).
