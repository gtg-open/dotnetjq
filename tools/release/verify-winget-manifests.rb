# frozen_string_literal: true

require "digest"
require "psych"
require "yaml"

def fail_validation(message)
  warn "WinGet manifest verification failed: #{message}"
  exit 1
end

unless ARGV.length == 6
  fail_validation(
    "usage: verify-winget-manifests.rb DIRECTORY PACKAGE_ID VERSION REPOSITORY TAG PUBLISHER",
  )
end

directory, package_id, version, repository, tag, publisher = ARGV
schema_version = "1.10.0"

def assert_unique_mapping_keys(node, location)
  case node
  when Psych::Nodes::Mapping
    keys = {}
    node.children.each_slice(2) do |key_node, value_node|
      unless key_node.is_a?(Psych::Nodes::Scalar)
        fail_validation("#{location} contains a non-scalar mapping key")
      end

      key = key_node.value
      fail_validation("#{location} contains duplicate key #{key.inspect}") if keys.key?(key)

      keys[key] = true
      assert_unique_mapping_keys(value_node, "#{location}.#{key}")
    end
  when Psych::Nodes::Sequence, Psych::Nodes::Document, Psych::Nodes::Stream
    node.children.each_with_index do |child, index|
      assert_unique_mapping_keys(child, "#{location}[#{index}]")
    end
  end
end

def load_manifest(path, manifest_type, schema_version)
  fail_validation("missing manifest #{path}") unless File.file?(path)

  text = File.binread(path)
  fail_validation("#{path} is not UTF-8") unless text.force_encoding(Encoding::UTF_8).valid_encoding?

  # Validate the submitted representation, not the LF-only source template:
  # canonicalize-winget-manifest.py emits pinned WingetCreate's producer line,
  # then the exact schema comment and blank separator, all with CRLF endings.
  unless text.end_with?("\r\n") && !text.gsub("\r\n", "").match?(/[\r\n]/)
    fail_validation("#{path} must use canonical CRLF line endings")
  end
  expected_header =
    "# Created using wingetcreate 1.12.13.0\r\n" \
    "# yaml-language-server: \$schema=https://aka.ms/winget-manifest.#{manifest_type}.#{schema_version}.schema.json\r\n\r\n"
  unless text.start_with?(expected_header)
    actual_header = text.each_line.take(3).join
    fail_validation("#{path} canonical header is #{actual_header.inspect}; expected #{expected_header.inspect}")
  end

  syntax_tree = Psych.parse_stream(text, filename: path)
  unless syntax_tree.children.length == 1
    fail_validation("#{path} must contain exactly one YAML document")
  end
  assert_unique_mapping_keys(syntax_tree, File.basename(path))
  document = YAML.safe_load(
    text,
    permitted_classes: [],
    permitted_symbols: [],
    aliases: false,
    filename: path,
  )
  fail_validation("#{path} must contain one YAML mapping") unless document.is_a?(Hash)

  document
rescue Psych::Exception => error
  fail_validation("#{path} is invalid YAML: #{error.message}")
end

def assert_exact_keys(mapping, expected, location)
  actual = mapping.keys
  return if actual.sort == expected.sort

  fail_validation(
    "#{location} keys differ: expected #{expected.sort.inspect}, got #{actual.sort.inspect}",
  )
end

def assert_value(actual, expected, location)
  return if actual == expected

  fail_validation("#{location} is #{actual.inspect}; expected #{expected.inspect}")
end

version_path = File.join(directory, "#{package_id}.yaml")
installer_path = File.join(directory, "#{package_id}.installer.yaml")
locale_path = File.join(directory, "#{package_id}.locale.en-US.yaml")

version_manifest = load_manifest(version_path, "version", schema_version)
assert_exact_keys(
  version_manifest,
  %w[PackageIdentifier PackageVersion DefaultLocale ManifestType ManifestVersion],
  File.basename(version_path),
)
assert_value(version_manifest["PackageIdentifier"], package_id, "version PackageIdentifier")
assert_value(version_manifest["PackageVersion"], version, "version PackageVersion")
assert_value(version_manifest["DefaultLocale"], "en-US", "version DefaultLocale")
assert_value(version_manifest["ManifestType"], "version", "version ManifestType")
assert_value(version_manifest["ManifestVersion"], schema_version, "version ManifestVersion")

installer_manifest = load_manifest(installer_path, "installer", schema_version)
assert_exact_keys(
  installer_manifest,
  %w[PackageIdentifier PackageVersion InstallerType Installers ManifestType ManifestVersion],
  File.basename(installer_path),
)
assert_value(installer_manifest["PackageIdentifier"], package_id, "installer PackageIdentifier")
assert_value(installer_manifest["PackageVersion"], version, "installer PackageVersion")
assert_value(installer_manifest["InstallerType"], "zip", "installer InstallerType")
assert_value(installer_manifest["ManifestType"], "installer", "installer ManifestType")
assert_value(installer_manifest["ManifestVersion"], schema_version, "installer ManifestVersion")

installers = installer_manifest["Installers"]
fail_validation("installer Installers must contain exactly x64 and arm64 entries") unless installers.is_a?(Array) && installers.length == 2

expected_architectures = %w[x64 arm64]
unless installers.all? { |installer| installer.is_a?(Hash) }
  fail_validation("installer Installers entries must be YAML mappings")
end
assert_value(installers.map { |installer| installer["Architecture"] }, expected_architectures, "installer architectures")
base_url = "https://github.com/#{repository}/releases/download/#{tag}"
installers.each do |installer|
  architecture = installer["Architecture"]
  assert_exact_keys(
    installer,
    %w[Architecture InstallerUrl InstallerSha256 NestedInstallerType NestedInstallerFiles],
    "installer #{architecture}",
  )
  rid = "win-#{architecture}"
  archive_name = "dotnetjq-#{version}-#{rid}.zip"
  expected_url = "#{base_url}/#{archive_name}"
  expected_hash = Digest::SHA256.file(File.join(directory, archive_name)).hexdigest.upcase
  assert_value(installer["InstallerUrl"], expected_url, "installer #{architecture} URL")
  assert_value(installer["InstallerSha256"], expected_hash, "installer #{architecture} SHA-256")
  assert_value(installer["NestedInstallerType"], "portable", "installer #{architecture} nested type")
  assert_value(
    installer["NestedInstallerFiles"],
    [{ "RelativeFilePath" => "dotnetjq.exe", "PortableCommandAlias" => "dotnetjq" }],
    "installer #{architecture} nested files",
  )
end

locale_manifest = load_manifest(locale_path, "defaultLocale", schema_version)
assert_exact_keys(
  locale_manifest,
  %w[PackageIdentifier PackageVersion PackageLocale Publisher PackageName PackageUrl License LicenseUrl ShortDescription ManifestType ManifestVersion],
  File.basename(locale_path),
)
repository_url = "https://github.com/#{repository}"
assert_value(locale_manifest["PackageIdentifier"], package_id, "locale PackageIdentifier")
assert_value(locale_manifest["PackageVersion"], version, "locale PackageVersion")
assert_value(locale_manifest["PackageLocale"], "en-US", "locale PackageLocale")
assert_value(locale_manifest["Publisher"], publisher, "locale Publisher")
assert_value(locale_manifest["PackageName"], "DotNetJq", "locale PackageName")
assert_value(locale_manifest["PackageUrl"], repository_url, "locale PackageUrl")
assert_value(locale_manifest["License"], "Multiple licenses; see LICENSES.md", "locale License")
assert_value(locale_manifest["LicenseUrl"], "#{repository_url}/blob/#{tag}/LICENSES.md", "locale LicenseUrl")
assert_value(
  locale_manifest["ShortDescription"],
  "jq 1.8.2-compatible command-line JSON processor for .NET and NativeAOT.",
  "locale ShortDescription",
)
assert_value(locale_manifest["ManifestType"], "defaultLocale", "locale ManifestType")
assert_value(locale_manifest["ManifestVersion"], schema_version, "locale ManifestVersion")

puts "WinGet manifests verified: #{package_id} #{version}"
