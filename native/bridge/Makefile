CXX ?= g++
CXXFLAGS ?= -shared -fPIC -O2 -std=c++14
LIBARIA2_DIR ?= /tmp/aria2_build/aria2-1.37.0
INCLUDES = -I. -I$(LIBARIA2_DIR)/src/includes
LDFLAGS = -L$(LIBARIA2_DIR)/src/.libs -laria2 -Wl,-rpath,'$$ORIGIN'
OUT_DIR ?= ../../runtimes/linux-x64/native
TARGET = $(OUT_DIR)/libaria2_bridge.so

all: $(TARGET)

$(TARGET): aria2_bridge.cpp aria2_bridge.h
	mkdir -p $(OUT_DIR)
	$(CXX) $(CXXFLAGS) $(INCLUDES) $< $(LDFLAGS) -o $@

clean:
	rm -f $(TARGET)

.PHONY: all clean
