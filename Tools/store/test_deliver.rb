module Deliver
  class UploadMetadata
    attr_reader :options, :attachment_calls

    def initialize(options)
      @options = options
      @attachment_calls = []
    end

    def review_attachment_file(version)
      @attachment_calls << version
    end
  end
end

$LOADED_FEATURES << "deliver/upload_metadata.rb"
load File.join(__dir__, "Deliverfile")

without_attachment = Deliver::UploadMetadata.new({})
without_attachment.review_attachment_file(:version)
raise "Unexpected attachment fetch" unless without_attachment.attachment_calls.empty?

with_attachment = Deliver::UploadMetadata.new(app_review_attachment_file: "review.pdf")
with_attachment.review_attachment_file(:version)
raise "Attachment upload was skipped" unless with_attachment.attachment_calls == [:version]

puts "PASS: absent review attachments are untouched; explicit uploads still delegate"