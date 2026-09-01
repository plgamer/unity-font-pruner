# Third-Party Notices

The MIT license in [LICENSE](LICENSE) covers the C# source in `Editor/` only.

This package also redistributes one prebuilt Java binary:

```
Editor/Tools~/bin/sfnttool.jar   (~10.7 MB, built 2016-11-18)
```

`sfnttool.jar` is an uber-jar: it bundles several third-party libraries into a
single archive. Their license texts ship inside the jar itself, at
`LICENSE.txt`, `license.html` and `unicode-license.txt` — you can read them with:

```sh
unzip -p Editor/Tools~/bin/sfnttool.jar LICENSE.txt
unzip -p Editor/Tools~/bin/sfnttool.jar license.html
unzip -p Editor/Tools~/bin/sfnttool.jar unicode-license.txt
```

## What is inside the jar

| Component | Java packages | Approx. classes | License |
|---|---|---|---|
| [sfntly](https://github.com/googlefonts/sfntly) — the actual subsetting engine, and the only part this tool invokes | `com.google.typography.font.*` | 524 | Apache-2.0 |
| [ICU4J](https://icu.unicode.org/) — accounts for most of the jar's size | `com.ibm.icu.*` | 3449 | Unicode/ICU License |
| [JCommander](https://jcommander.org/) — sfnttool's CLI argument parsing | `com.beust.jcommander.*` | 51 | Apache-2.0 |
| [JUnit 4](https://junit.org/junit4/) | `org.junit.*`, `junit.*` | 256 | EPL-1.0 |
| [Hamcrest](https://hamcrest.org/JavaHamcrest/) | `org.hamcrest.*` | 26 | BSD-3-Clause |

JUnit and Hamcrest are test-time dependencies that were captured by whatever
build produced this jar; 34 test classes are bundled alongside them. They are
never invoked at runtime. They are listed here because they are redistributed,
not because the tool needs them.

All five licenses permit redistribution in binary form provided the notices are
preserved, which this file and the license texts inside the jar do.

## Prior art

The `Tools~` layout and the overall prune-by-charset approach follow the
original Python **FontPruner** by gulu-dev:
<http://gulu-dev.com/post/2017/2017-09-15-font-pruner-intro/content>

No code from that project is redistributed here. Its two helper jars
(`GenFileList.jar`, `fontExtract.jar`) and Python scripts were part of an
earlier pipeline that this C# rewrite replaced, and have been removed.
