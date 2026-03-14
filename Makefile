MCS    = mcs
REFS   = -r:System.dll -r:System.Core.dll \
         -r:System.Net.Http.dll \
         -r:System.Web.Extensions.dll \
         -r:System.Runtime.Serialization.dll
SRC    = src/Exceptions.cs src/ReparatioResult.cs src/ReparatioClient.cs

all: bin/Reparatio.dll

bin/Reparatio.dll: $(SRC)
	mkdir -p bin
	$(MCS) -target:library -out:bin/Reparatio.dll $(REFS) $(SRC)

bin/Tests.exe: bin/Reparatio.dll tests/ReparatioClientTests.cs
	$(MCS) -target:exe -out:bin/Tests.exe \
	    -r:bin/Reparatio.dll $(REFS) \
	    tests/ReparatioClientTests.cs

test: bin/Tests.exe
	mono bin/Tests.exe

clean:
	rm -rf bin

.PHONY: all test clean
