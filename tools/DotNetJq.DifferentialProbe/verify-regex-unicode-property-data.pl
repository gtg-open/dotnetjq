#!/usr/bin/env perl

use strict;
use warnings;
use MIME::Base64 qw(decode_base64);

my ($unicode_path, $posix_path, $catalog_path, $aliases_path, $focused_path) = @ARGV;
die "usage: $0 unicode_property_data.c unicode_property_data_posix.c " .
    "OnigurumaUnicodePropertyCatalog.cs OnigurumaUnicodePropertyAliases.cs " .
    "OnigurumaUnicodePropertyData.cs\n"
    if @ARGV != 5;

my $unicode = slurp($unicode_path);
my $posix = slurp($posix_path);
my $catalog_source = slurp($catalog_path);
my $aliases_source = slurp($aliases_path);
my $focused_source = slurp($focused_path);

my $main_tables = parse_c_tables($unicode);
my $posix_tables = parse_c_tables($posix);
my $catalog = parse_csharp_dictionary($catalog_source, 'EncodedRanges');
my $additional = parse_csharp_dictionary($aliases_source, 'AdditionalEncodedRanges');
my $actual_aliases = parse_csharp_dictionary($aliases_source, 'PropertyAliases');

assert_count('physical Unicode arrays', $main_tables, 612);
assert_count('POSIX arrays', $posix_tables, 15);
assert_count('encoded Unicode tables', $catalog, 612);
assert_count('encoded POSIX tables', $additional, 15);
assert_count('managed aliases', $actual_aliases, 886);

compare_encoded_tables('Unicode', $main_tables, $catalog);
compare_encoded_tables('POSIX', $posix_tables, $additional);

my %macros = $unicode =~ /^#define CR_([A-Za-z0-9_]+) CR_([A-Za-z0-9_]+)\s*$/mg;
$unicode =~ /const CodeRanges\[\]\s*=\s*\{(.*?)^\};/ms
    or die "CodeRanges table not found\n";
my @code_ranges = ($1 =~ /\bCR_([A-Za-z0-9_]+)/g);
die "expected 629 CodeRanges entries, got " . scalar(@code_ranges) . "\n"
    if @code_ranges != 629;

my %pool = $unicode =~ /unicode_prop_name_pool_str(\d+)\[sizeof\("([^"]+)"\)\]/g;
$unicode =~ /wordlist\[\]\s*=\s*\{(.*?)^\s*\};/ms
    or die "gperf wordlist not found\n";
my $wordlist = $1;
my %expected_aliases;
while ($wordlist =~ /\{pool_offset\((\d+)\),\s*(\d+)\}/g) {
    my ($pool_id, $range_index) = ($1, $2);
    die "missing string-pool entry $pool_id\n" if !exists($pool{$pool_id});
    die "CodeRanges index $range_index is out of bounds\n"
        if $range_index >= @code_ranges;
    my $target = $code_ranges[$range_index];
    my %seen;
    while (exists($macros{$target})) {
        die "macro cycle at $target\n" if $seen{$target}++;
        $target = $macros{$target};
    }
    $expected_aliases{normalize($pool{$pool_id})} = normalize($target);
}
assert_count('upstream gperf aliases', \%expected_aliases, 886);

compare_dictionary('property alias', \%expected_aliases, $actual_aliases);
for my $target (values(%$actual_aliases)) {
    die "alias target '$target' has no encoded table\n"
        if !exists($catalog->{$target}) && !exists($additional->{$target});
}

my @focused_names = qw(
    Emoji
    Emoji_Component
    Emoji_Modifier
    Emoji_Modifier_Base
    Emoji_Presentation
    Extended_Pictographic
);
my $focused_range_count = 0;
for my $name (@focused_names) {
    my $key = normalize($name);
    die "focused upstream table '$name' is missing\n" if !exists($main_tables->{$key});
    my $actual = parse_csharp_array($focused_source, $name);
    my $wanted = join(',', @{$main_tables->{$key}});
    my $got = join(',', @$actual);
    die "focused managed table '$name' range mismatch\n" if $got ne $wanted;
    $focused_range_count += @$actual / 2;
}
die "expected 359 focused Unicode ranges, got $focused_range_count\n"
    if $focused_range_count != 359;

print "verified physical_unicode=612 posix=15 code_ranges=629 aliases=886 " .
    "decoded_range_tables=627 focused_tables=6 focused_ranges=359\n";

sub slurp {
    my ($path) = @_;
    open(my $handle, '<', $path) or die "cannot read $path: $!\n";
    local $/;
    return <$handle>;
}

sub parse_c_tables {
    my ($source) = @_;
    my %tables;
    while ($source =~ /^CR_([A-Za-z0-9_]+)\[\]\s*=\s*\{\s*(\d+)\s*,(.*?)^\};\s*\/\* END of CR_\1 \*\//msg) {
        my ($name, $declared_count, $body) = ($1, $2, $3);
        my @points = map { hex($_) } ($body =~ /0x([0-9a-fA-F]+)/g);
        die "table '$name' declares $declared_count ranges but has " . (@points / 2) . "\n"
            if @points != $declared_count * 2;
        my $key = normalize($name);
        die "duplicate normalized table '$key'\n" if exists($tables{$key});
        $tables{$key} = \@points;
    }
    return \%tables;
}

sub parse_csharp_dictionary {
    my ($source, $member) = @_;
    $source =~ /\b\Q$member\E\b\s*=.*?new Dictionary<string, string>\(StringComparer\.Ordinal\)\s*\{(.*?)\}\s*\.ToFrozenDictionary/s
        or die "managed dictionary '$member' not found\n";
    my $body = $1;
    my %dictionary;
    while ($body =~ /^\s*\["([A-Z0-9]+)"\]\s*=\s*"([A-Za-z0-9+\/=]+)",?\s*$/mg) {
        die "duplicate managed key '$1' in $member\n" if exists($dictionary{$1});
        $dictionary{$1} = $2;
    }
    return \%dictionary;
}

sub parse_csharp_array {
    my ($source, $member) = @_;
    $source =~ /\b\Q$member\E\b\s*=\s*\[(.*?)\];/s
        or die "managed array '$member' not found\n";
    my @points = map { hex($_) } ($1 =~ /0x([0-9a-fA-F]+)/g);
    die "managed array '$member' has an odd endpoint count\n" if @points % 2 != 0;
    return \@points;
}

sub compare_encoded_tables {
    my ($label, $expected, $actual) = @_;
    for my $key (sort keys(%$expected)) {
        die "$label table '$key' is missing from managed data\n" if !exists($actual->{$key});
        my $decoded = decode_deltas($actual->{$key});
        my $wanted = join(',', @{$expected->{$key}});
        my $got = join(',', @$decoded);
        die "$label table '$key' range mismatch\n" if $got ne $wanted;
    }
    for my $key (sort keys(%$actual)) {
        die "managed $label table '$key' has no upstream source table\n"
            if !exists($expected->{$key});
    }
}

sub decode_deltas {
    my ($encoded) = @_;
    my @bytes = unpack('C*', decode_base64($encoded));
    my @values;
    my $previous = 0;
    for (my $index = 0; $index < @bytes;) {
        my ($delta, $shift, $current) = (0, 0, 0);
        do {
            die "truncated LEB128 sequence\n" if $index >= @bytes;
            $current = $bytes[$index++];
            $delta |= ($current & 0x7f) << $shift;
            $shift += 7;
            die "oversized LEB128 sequence\n" if $shift > 35;
        } while (($current & 0x80) != 0);
        $previous += $delta;
        push(@values, $previous);
    }
    die "decoded endpoint count is odd\n" if @values % 2 != 0;
    return \@values;
}

sub compare_dictionary {
    my ($label, $expected, $actual) = @_;
    for my $key (sort keys(%$expected)) {
        die "$label '$key' is missing\n" if !exists($actual->{$key});
        die "$label '$key' expected '$expected->{$key}', got '$actual->{$key}'\n"
            if $actual->{$key} ne $expected->{$key};
    }
    for my $key (sort keys(%$actual)) {
        die "managed $label '$key' has no upstream keyword\n"
            if !exists($expected->{$key});
    }
}

sub assert_count {
    my ($label, $values, $expected) = @_;
    my $actual = scalar(keys(%$values));
    die "expected $expected $label, got $actual\n" if $actual != $expected;
}

sub normalize {
    my ($name) = @_;
    $name = uc($name);
    $name =~ s/[^A-Z0-9]//g;
    return $name;
}
