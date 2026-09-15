# Third-party notices

portmark includes material from the projects below. Each is used under the licence stated.

## The USB ID Repository (usb.ids)

- **What is used:** the vendor lines only (USB vendor ID and the name registered to it), embedded
  in `Portmark.Core` as `src/Portmark.Core/Usb/usb-vendors.tsv`. Product, interface and class
  lines are not included
- **Source:** http://www.linux-usb.org/usb.ids
- **Version:** 2026.06.26, dated 2026-06-26 20:34:02
- **Maintainer:** Stephen J. Gowdy, with entries contributed by the repository's users
- **Licence:** the repository states on http://www.linux-usb.org/usb-ids.html that "the contents
  of the database and the generated files can be distributed under the terms of either the GNU
  General Public License (version 2 or later) or of the 3-clause BSD License." portmark uses it
  under the 3-clause BSD License, reproduced below. The usb.ids file itself carries no licence
  text and names no copyright holder, so the copyright line below names the contributors
  collectively rather than quoting one

To refresh the table, run `scripts/update-usb-vendors.ps1`, confirm the licence statement above
is unchanged, and update the version and date here.

```
BSD 3-Clause License

Copyright (c) the contributors to The USB ID Repository

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
